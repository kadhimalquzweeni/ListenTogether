using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Creates an ephemeral P-256 ECDH offer and derives independent directional
/// media keys after receiving the other participant's offer. Offers must be
/// exchanged over the authenticated SignalR session, never an untrusted channel.
/// </summary>
/// <remarks>
/// The proof returned by this class confirms possession of the negotiated secret;
/// it does not authenticate a peer against a malicious signalling server. The
/// current trust boundary is the TLS-protected signalling service and its
/// session membership checks. Add an independently verifiable peer identity
/// (for example, a user-verified fingerprint) before claiming server-resistant
/// end-to-end authentication.
/// </remarks>
public sealed class PeerKeyAgreement : IDisposable
{
    private readonly ECDiffieHellman _ephemeralKey;
    private readonly byte[] _localNonce;
    private bool _completed;
    private bool _disposed;

    private PeerKeyAgreement(Guid sessionId, string localConnectionId, string remoteConnectionId)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(localConnectionId))
            throw new ArgumentException("A local connection ID is required.", nameof(localConnectionId));
        if (string.IsNullOrWhiteSpace(remoteConnectionId))
            throw new ArgumentException("A remote connection ID is required.", nameof(remoteConnectionId));
        if (string.Equals(localConnectionId, remoteConnectionId, StringComparison.Ordinal))
            throw new ArgumentException("Peer connection IDs must be different.", nameof(remoteConnectionId));

        SessionId = sessionId;
        LocalConnectionId = localConnectionId;
        RemoteConnectionId = remoteConnectionId;
        _ephemeralKey = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        _localNonce = RandomNumberGenerator.GetBytes(32);
        LocalOffer = new PeerKeyOffer(
            sessionId,
            localConnectionId,
            remoteConnectionId,
            _ephemeralKey.ExportSubjectPublicKeyInfo(),
            _localNonce.ToArray());
    }

    public Guid SessionId { get; }
    public string LocalConnectionId { get; }
    public string RemoteConnectionId { get; }
    public PeerKeyOffer LocalOffer { get; }

    public static PeerKeyAgreement Create(
        Guid sessionId,
        string localConnectionId,
        string remoteConnectionId) =>
        new(sessionId, localConnectionId, remoteConnectionId);

    public PeerKeyAgreementResult Complete(PeerKeyOffer remoteOffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
            throw new InvalidOperationException("This key agreement has already been completed.");
        ArgumentNullException.ThrowIfNull(remoteOffer);

        if (remoteOffer.SessionId != SessionId ||
            !string.Equals(remoteOffer.FromConnectionId, RemoteConnectionId, StringComparison.Ordinal) ||
            !string.Equals(remoteOffer.ToConnectionId, LocalConnectionId, StringComparison.Ordinal))
            throw new CryptographicException("The peer offer is not bound to this session and connection pair.");

        if (remoteOffer.PublicKey is null || remoteOffer.PublicKey.Length is < 64 or > 512 ||
            remoteOffer.Nonce is null || remoteOffer.Nonce.Length != 32)
            throw new CryptographicException("The peer offer contains invalid key material.");

        using var remoteKey = ECDiffieHellman.Create();
        try
        {
            remoteKey.ImportSubjectPublicKeyInfo(remoteOffer.PublicKey, out var bytesRead);
            if (bytesRead != remoteOffer.PublicKey.Length)
                throw new CryptographicException("The peer public key contains trailing data.");
        }
        catch (Exception exception) when (exception is ArgumentException or CryptographicException)
        {
            throw new CryptographicException("The peer public key is invalid.", exception);
        }

        var sharedSecret = _ephemeralKey.DeriveRawSecretAgreement(remoteKey.PublicKey);
        var transcript = BuildTranscript(LocalOffer, remoteOffer);
        var salt = SHA256.HashData(transcript);
        var info = Encoding.UTF8.GetBytes("ListenTogether/peer-key-agreement/v1");
        var material = new byte[136]; // confirmation key (32) + two directional key/prefix pairs (72 each)
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, material, salt, info);
            var confirmationKey = material.AsSpan(0, 32);
            var firstKey = material.AsSpan(32, 32).ToArray();
            var firstPrefix = material.AsSpan(64, 4).ToArray();
            var secondKey = material.AsSpan(68, 32).ToArray();
            var secondPrefix = material.AsSpan(100, 4).ToArray();

            var localIsFirst = string.CompareOrdinal(LocalConnectionId, RemoteConnectionId) < 0;
            var sendKey = localIsFirst ? firstKey : secondKey;
            var sendPrefix = localIsFirst ? firstPrefix : secondPrefix;
            var receiveKey = localIsFirst ? secondKey : firstKey;
            var receivePrefix = localIsFirst ? secondPrefix : firstPrefix;

            var localProof = CreateProof(confirmationKey, transcript, LocalConnectionId);
            var expectedRemoteProof = CreateProof(confirmationKey, transcript, RemoteConnectionId);
            _completed = true;

            return new PeerKeyAgreementResult(
                sendKey, sendPrefix, receiveKey, receivePrefix,
                localProof, expectedRemoteProof, SHA256.HashData(transcript));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sharedSecret);
            CryptographicOperations.ZeroMemory(material);
            CryptographicOperations.ZeroMemory(salt);
            CryptographicOperations.ZeroMemory(transcript);
        }
    }

    private static byte[] CreateProof(ReadOnlySpan<byte> key, ReadOnlySpan<byte> transcript, string connectionId)
    {
        var label = Encoding.UTF8.GetBytes("ListenTogether/peer-confirmation/v1/" + connectionId);
        var data = new byte[transcript.Length + label.Length];
        transcript.CopyTo(data);
        label.CopyTo(data.AsSpan(transcript.Length));
        try
        {
            return HMACSHA256.HashData(key, data);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(data);
        }
    }

    private static byte[] BuildTranscript(PeerKeyOffer local, PeerKeyOffer remote)
    {
        var first = string.CompareOrdinal(local.FromConnectionId, remote.FromConnectionId) < 0
            ? local : remote;
        var second = ReferenceEquals(first, local) ? remote : local;

        using var stream = new MemoryStream();
        stream.Write(Encoding.UTF8.GetBytes("ListenTogether/peer-transcript/v1"));
        Span<byte> session = stackalloc byte[16];
        local.SessionId.TryWriteBytes(session);
        stream.Write(session);
        WriteField(stream, first.FromConnectionId);
        WriteField(stream, second.FromConnectionId);
        WriteField(stream, first.PublicKey);
        WriteField(stream, first.Nonce);
        WriteField(stream, second.PublicKey);
        WriteField(stream, second.Nonce);
        return stream.ToArray();
    }

    private static void WriteField(Stream stream, string value) =>
        WriteField(stream, Encoding.UTF8.GetBytes(value));

    private static void WriteField(Stream stream, byte[] value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        stream.Write(length);
        stream.Write(value);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ephemeralKey.Dispose();
        CryptographicOperations.ZeroMemory(_localNonce);
    }
}

public sealed record PeerKeyOffer(
    Guid SessionId,
    string FromConnectionId,
    string ToConnectionId,
    byte[] PublicKey,
    byte[] Nonce);

/// <summary>
/// Holds directional key material. Call VerifyPeerProof before constructing
/// UdpSecurePacketCodec instances or sending any media.
/// </summary>
public sealed class PeerKeyAgreementResult : IDisposable
{
    private bool _disposed;

    internal PeerKeyAgreementResult(
        byte[] sendKey, byte[] sendNoncePrefix,
        byte[] receiveKey, byte[] receiveNoncePrefix,
        byte[] localProof, byte[] expectedPeerProof, byte[] transcriptHash)
    {
        SendKey = sendKey;
        SendNoncePrefix = sendNoncePrefix;
        ReceiveKey = receiveKey;
        ReceiveNoncePrefix = receiveNoncePrefix;
        LocalProof = localProof;
        ExpectedPeerProof = expectedPeerProof;
        TranscriptHash = transcriptHash;
    }

    public byte[] SendKey { get; }
    public byte[] SendNoncePrefix { get; }
    public byte[] ReceiveKey { get; }
    public byte[] ReceiveNoncePrefix { get; }
    public byte[] LocalProof { get; }
    public byte[] TranscriptHash { get; }

    private byte[] ExpectedPeerProof { get; }

    public bool VerifyPeerProof(ReadOnlySpan<byte> proof)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return proof.Length == ExpectedPeerProof.Length &&
            CryptographicOperations.FixedTimeEquals(proof, ExpectedPeerProof);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        CryptographicOperations.ZeroMemory(SendKey);
        CryptographicOperations.ZeroMemory(SendNoncePrefix);
        CryptographicOperations.ZeroMemory(ReceiveKey);
        CryptographicOperations.ZeroMemory(ReceiveNoncePrefix);
        CryptographicOperations.ZeroMemory(LocalProof);
        CryptographicOperations.ZeroMemory(ExpectedPeerProof);
        CryptographicOperations.ZeroMemory(TranscriptHash);
    }
}
