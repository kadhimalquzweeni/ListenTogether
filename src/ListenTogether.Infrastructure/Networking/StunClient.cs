using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Sends RFC 8489 STUN Binding requests over an existing UDP socket.
/// Reusing the peer's media socket is important: the server-reflexive endpoint
/// returned by STUN must describe the same socket that will later send probes.
/// </summary>
public sealed class StunClient
{
    private const int HeaderLength = 20;
    private const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccessResponse = 0x0101;
    private const ushort XorMappedAddress = 0x0020;
    private const ushort MappedAddress = 0x0001;
    private const int MaximumDatagramLength = 512;

    public async Task<IPEndPoint> GetMappedEndPointAsync(
        UdpClient socket,
        IPEndPoint stunServer,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(stunServer);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var transactionId = RandomNumberGenerator.GetBytes(12);
        var request = CreateBindingRequest(transactionId);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            await socket.SendAsync(request, stunServer, timeoutSource.Token).ConfigureAwait(false);

            while (true)
            {
                var received = await socket.ReceiveAsync(timeoutSource.Token).ConfigureAwait(false);
                // Do not accept a spoofed response from an arbitrary UDP source.
                if (!received.RemoteEndPoint.Equals(stunServer))
                    continue;

                if (TryParseBindingResponse(received.Buffer, transactionId, out var mapped))
                    return mapped;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The STUN server at {stunServer} did not respond within {timeout}.");
        }
    }

    internal static byte[] CreateBindingRequest(ReadOnlySpan<byte> transactionId)
    {
        if (transactionId.Length != 12)
            throw new ArgumentException("A STUN transaction ID must contain 12 bytes.", nameof(transactionId));

        var request = new byte[HeaderLength];
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(4, 4), MagicCookie);
        transactionId.CopyTo(request.AsSpan(8, 12));
        return request;
    }

    internal static bool TryParseBindingResponse(
        ReadOnlySpan<byte> response,
        ReadOnlySpan<byte> transactionId,
        out IPEndPoint mappedEndPoint)
    {
        mappedEndPoint = null!;
        if (response.Length < HeaderLength || response.Length > MaximumDatagramLength ||
            transactionId.Length != 12)
            return false;

        var messageType = BinaryPrimitives.ReadUInt16BigEndian(response[..2]);
        var messageLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(2, 2));
        var cookie = BinaryPrimitives.ReadUInt32BigEndian(response.Slice(4, 4));
        if (messageType != BindingSuccessResponse ||
            cookie != MagicCookie ||
            messageLength % 4 != 0 ||
            response.Length != HeaderLength + messageLength ||
            !CryptographicOperations.FixedTimeEquals(response.Slice(8, 12), transactionId))
            return false;

        var offset = HeaderLength;
        while (offset < response.Length)
        {
            if (response.Length - offset < 4)
                return false;

            var type = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset, 2));
            var valueLength = BinaryPrimitives.ReadUInt16BigEndian(response.Slice(offset + 2, 2));
            var paddedLength = (valueLength + 3) & ~3;
            if (paddedLength > response.Length - offset - 4)
                return false;

            var value = response.Slice(offset + 4, valueLength);
            if (type is XorMappedAddress or MappedAddress)
            {
                if (TryParseAddressAttribute(value, type == XorMappedAddress, transactionId, out mappedEndPoint))
                    return true;
                return false;
            }

            offset += 4 + paddedLength;
        }

        return false;
    }

    private static bool TryParseAddressAttribute(
        ReadOnlySpan<byte> value,
        bool xor,
        ReadOnlySpan<byte> transactionId,
        out IPEndPoint endPoint)
    {
        endPoint = null!;
        if (value.Length < 4 || value[0] != 0)
            return false;

        var family = value[1];
        var addressLength = family switch
        {
            0x01 => 4,
            0x02 => 16,
            _ => 0
        };
        if (addressLength == 0 || value.Length != 4 + addressLength)
            return false;

        var port = BinaryPrimitives.ReadUInt16BigEndian(value.Slice(2, 2));
        if (xor)
            port ^= (ushort)(MagicCookie >> 16);

        var addressBytes = value.Slice(4, addressLength).ToArray();
        if (xor)
        {
            Span<byte> mask = stackalloc byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(mask[..4], MagicCookie);
            transactionId.CopyTo(mask[4..]);
            for (var index = 0; index < addressBytes.Length; index++)
                addressBytes[index] ^= mask[index];
        }

        endPoint = new IPEndPoint(new IPAddress(addressBytes), port);
        return true;
    }
}
