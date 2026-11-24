using System.Buffers.Binary;
using System.Security.Cryptography;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// AES-256-GCM protection for direct-media UDP datagrams. Each direction must
/// use a distinct key and nonce prefix. Sequence values must never repeat.
/// </summary>
public sealed class UdpSecurePacketCodec : IDisposable
{
    public const int MaximumDatagramSize = 1200;
    public const int AuthenticationTagSize = 16;
    public const int HeaderSize = 34;
    private const uint Magic = 0x4C545544; // LTUD
    private const byte Version = 1;
    private readonly byte[] _key;
    private readonly byte[] _noncePrefix;
    private readonly object _replayLock = new();
    private ulong _highestReceivedSequence;
    private ulong _receivedWindow;
    private bool _disposed;

    public UdpSecurePacketCodec(ReadOnlySpan<byte> key, ReadOnlySpan<byte> noncePrefix)
    {
        if (key.Length != 32)
            throw new ArgumentException("A 256-bit AES-GCM key is required.", nameof(key));
        if (noncePrefix.Length != 4)
            throw new ArgumentException("A 4-byte nonce prefix is required.", nameof(noncePrefix));
        _key = key.ToArray();
        _noncePrefix = noncePrefix.ToArray();
    }

    public byte[] Encrypt(Guid sessionId, UdpPacketType packetType, ulong sequence, ReadOnlySpan<byte> plaintext)
    {
        ThrowIfDisposed();
        if (!Enum.IsDefined(packetType))
            throw new ArgumentOutOfRangeException(nameof(packetType));
        if (sequence == 0)
            throw new ArgumentOutOfRangeException(nameof(sequence), "Sequence zero is reserved.");
        if (HeaderSize + plaintext.Length + AuthenticationTagSize > MaximumDatagramSize)
            throw new ArgumentOutOfRangeException(nameof(plaintext), "Protected datagram exceeds the transport limit.");

        var packet = new byte[HeaderSize + plaintext.Length + AuthenticationTagSize];
        var header = packet.AsSpan(0, HeaderSize);
        BinaryPrimitives.WriteUInt32BigEndian(header, Magic);
        header[4] = Version;
        header[5] = (byte)packetType;
        sessionId.TryWriteBytes(header.Slice(6, 16));
        BinaryPrimitives.WriteUInt64BigEndian(header.Slice(22, 8), sequence);
        _noncePrefix.CopyTo(header.Slice(30, 4));

        Span<byte> nonce = stackalloc byte[12];
        _noncePrefix.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4), sequence);
        using var aes = new AesGcm(_key, AuthenticationTagSize);
        aes.Encrypt(nonce, plaintext,
            packet.AsSpan(HeaderSize, plaintext.Length),
            packet.AsSpan(HeaderSize + plaintext.Length, AuthenticationTagSize), header);
        return packet;
    }

    public bool TryDecrypt(ReadOnlySpan<byte> datagram, Guid expectedSessionId, out UdpSecurePacket? packet)
    {
        ThrowIfDisposed();
        packet = null;
        if (datagram.Length < HeaderSize + AuthenticationTagSize || datagram.Length > MaximumDatagramSize)
            return false;

        var header = datagram[..HeaderSize];
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != Magic || header[4] != Version ||
            !Enum.IsDefined((UdpPacketType)header[5]))
            return false;
        if (new Guid(header.Slice(6, 16)) != expectedSessionId)
            return false;

        var sequence = BinaryPrimitives.ReadUInt64BigEndian(header.Slice(22, 8));
        if (sequence == 0 || !_noncePrefix.AsSpan().SequenceEqual(header.Slice(30, 4)))
            return false;

        lock (_replayLock)
        {
            if (IsSequenceAlreadyReceived(sequence))
                return false;

            var cipherLength = datagram.Length - HeaderSize - AuthenticationTagSize;
            var plaintext = new byte[cipherLength];
            Span<byte> nonce = stackalloc byte[12];
            _noncePrefix.CopyTo(nonce);
            BinaryPrimitives.WriteUInt64BigEndian(nonce.Slice(4), sequence);
            try
            {
                using var aes = new AesGcm(_key, AuthenticationTagSize);
                aes.Decrypt(nonce, datagram.Slice(HeaderSize, cipherLength),
                    datagram[^AuthenticationTagSize..], plaintext, header);
            }
            catch (CryptographicException)
            {
                CryptographicOperations.ZeroMemory(plaintext);
                return false;
            }

            CommitSequence(sequence);
            packet = new UdpSecurePacket((UdpPacketType)header[5], sequence, plaintext);
            return true;
        }
    }

    private bool IsSequenceAlreadyReceived(ulong sequence)
    {
        if (sequence > _highestReceivedSequence)
            return false;
        var distance = _highestReceivedSequence - sequence;
        return distance >= 64 || (_receivedWindow & (1UL << (int)distance)) != 0;
    }

    private void CommitSequence(ulong sequence)
    {
        if (sequence > _highestReceivedSequence)
        {
            var distance = sequence - _highestReceivedSequence;
            _receivedWindow = distance >= 64 ? 1UL : (_receivedWindow << (int)distance) | 1UL;
            _highestReceivedSequence = sequence;
            return;
        }
        _receivedWindow |= 1UL << (int)(_highestReceivedSequence - sequence);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        CryptographicOperations.ZeroMemory(_noncePrefix);
    }
}

public enum UdpPacketType : byte
{
    Audio = 1,
    ConnectivityCheck = 2,
    ConnectivityCheckResponse = 3,
    KeepAlive = 4
}

public sealed record UdpSecurePacket(UdpPacketType Type, ulong Sequence, byte[] Payload);
