using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using ListenTogether.Core.Sessions;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Establishes authenticated UDP paths to secured session peers and carries
/// encrypted Opus frames over those paths. SignalR is used only for signalling.
/// </summary>
public sealed class UdpPeerTransportCoordinator : IAsyncDisposable
{
    private const int ConnectivityChallengeLength = 16;
    private const int KeepAliveIntervalMilliseconds = 5_000;
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(15);
    private readonly UdpPeerCandidateCoordinator _candidates;
    private readonly PeerKeyNegotiationCoordinator _keyNegotiation;
    private readonly Guid _sessionId;
    private readonly ConcurrentDictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Task? _receiveTask;
    private Task? _keepAliveTask;
    private int _started;
    private int _disposed;

    public UdpPeerTransportCoordinator(
        UdpPeerCandidateCoordinator candidates,
        PeerKeyNegotiationCoordinator keyNegotiation,
        Guid sessionId)
    {
        _candidates = candidates ?? throw new ArgumentNullException(nameof(candidates));
        _keyNegotiation = keyNegotiation ?? throw new ArgumentNullException(nameof(keyNegotiation));
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A session ID is required.", nameof(sessionId));
        _sessionId = sessionId;

        _candidates.RemoteCandidatesUpdated += OnRemoteCandidatesUpdated;
        _candidates.RemotePeerLeft += OnRemotePeerLeft;
        _keyNegotiation.PeerSecured += OnPeerSecured;
    }

    /// <summary>Raised when an authenticated UDP path is established or re-established.</summary>
    public event Action<string, IPEndPoint>? PeerConnected;

    /// <summary>Raised when a peer's direct UDP connectivity checks begin or retry.</summary>
    public event Action<string>? PeerConnectionAttemptStarted;

    /// <summary>Raised when a connectivity-check round cannot establish an authenticated path.</summary>
    public event Action<string, string>? PeerConnectionFailed;

    /// <summary>Raised for authenticated Opus frames received directly from a peer.</summary>
    public event Action<string, byte[], ulong>? AudioPacketReceived;

    public IReadOnlyCollection<string> ConnectedPeers =>
        _peers.Where(pair => pair.Value.GetActiveEndpoint(PeerTimeout) is not null)
            .Select(pair => pair.Key)
            .ToArray();

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_candidates.LocalCandidateSet is null)
            throw new InvalidOperationException("UDP candidates must be gathered before starting the peer transport.");
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("The UDP peer transport has already been started.");

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cancellation.Token;
        _receiveTask = ReceiveLoopAsync(token);
        _keepAliveTask = KeepAliveLoopAsync(token);

        foreach (var peerId in _keyNegotiation.SecuredPeers)
            EnsurePeer(peerId);

        foreach (var pair in _peers)
            StartConnectivityChecks(pair.Key, pair.Value);
        await Task.CompletedTask;
    }

    /// <summary>
    /// Sends one encoded Opus frame only to currently connected direct peers.
    /// Frames are dropped for peers without an authenticated UDP path; there is no relay fallback.
    /// </summary>
    public async Task<IReadOnlyCollection<string>> SendAudioToConnectedPeersAsync(
        ReadOnlyMemory<byte> opusPayload,
        long audioSequence,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _started) == 0)
            return Array.Empty<string>();
        const int audioSequenceHeaderLength = sizeof(ulong);
        if (audioSequence <= 0 || opusPayload.Length is < 1 or > UdpSecurePacketCodec.MaximumDatagramSize -
            UdpSecurePacketCodec.HeaderSize - UdpSecurePacketCodec.AuthenticationTagSize - audioSequenceHeaderLength)
            return Array.Empty<string>();

        var socket = _candidates.LocalCandidateSet?.TransportSocket;
        if (socket is null)
            return Array.Empty<string>();

        var opusBytes = opusPayload.ToArray();
        var payload = new byte[sizeof(ulong) + opusBytes.Length];
        BinaryPrimitives.WriteUInt64BigEndian(payload, (ulong)audioSequence);
        Buffer.BlockCopy(opusBytes, 0, payload, sizeof(ulong), opusBytes.Length);
        var sent = new List<string>();
        foreach (var pair in _peers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = pair.Value.GetActiveEndpoint(PeerTimeout);
            if (endpoint is null)
            {
                StartConnectivityChecks(pair.Key, pair.Value);
                continue;
            }

            try
            {
                var datagram = pair.Value.CreatePacket(
                    _sessionId, UdpPacketType.Audio, payload);
                await socket.SendAsync(datagram, endpoint, cancellationToken).ConfigureAwait(false);
                sent.Add(pair.Key);
            }
            catch (Exception exception) when (
                exception is SocketException or ObjectDisposedException or OperationCanceledException)
            {
                pair.Value.ClearEndpoint();
                StartConnectivityChecks(pair.Key, pair.Value);
                if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    throw;
            }
        }

        return sent;
    }

    private void OnPeerSecured(string peerId, PeerKeyAgreementResult result)
    {
        if (Volatile.Read(ref _disposed) == 1)
            return;
        var state = EnsurePeer(peerId);
        if (state is not null)
            StartConnectivityChecks(peerId, state);
    }

    private void OnRemoteCandidatesUpdated(string peerId, IReadOnlyList<PeerCandidate> _)
    {
        if (Volatile.Read(ref _disposed) == 1 ||
            !_peers.TryGetValue(peerId, out var state))
            return;
        StartConnectivityChecks(peerId, state);
    }

    private void OnRemotePeerLeft(string peerId)
    {
        if (_peers.TryRemove(peerId, out var state))
            state.Dispose();
    }

    private PeerState? EnsurePeer(string peerId)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            return null;
        if (_peers.TryGetValue(peerId, out var existing))
            return existing;

        var result = _keyNegotiation.GetSecuredResult(peerId);
        if (result is null)
            return null;

        var created = new PeerState(result);
        var state = _peers.GetOrAdd(peerId, created);
        if (!ReferenceEquals(state, created))
            created.Dispose();
        return state;
    }

    private void StartConnectivityChecks(string peerId, PeerState state)
    {
        if (Volatile.Read(ref _started) == 0 ||
            _cancellation is null ||
            _cancellation.IsCancellationRequested ||
            state.GetActiveEndpoint(PeerTimeout) is not null ||
            !state.TryStartChecks())
            return;

        RaisePeerConnectionAttemptStarted(peerId);
        _ = RunConnectivityChecksAsync(peerId, state, _cancellation.Token);
    }

    private async Task RunConnectivityChecksAsync(
        string peerId,
        PeerState state,
        CancellationToken cancellationToken)
    {
        try
        {
            var socket = _candidates.LocalCandidateSet?.TransportSocket;
            if (socket is null)
                return;

            for (var attempt = 0; attempt < 12; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (state.GetActiveEndpoint(PeerTimeout) is not null)
                    return;

                var candidates = _candidates.GetRemoteCandidates(peerId);
                if (candidates.Count == 0)
                {
                    RaisePeerConnectionFailed(peerId, "No remote UDP candidates have arrived yet. Waiting for candidate exchange and retrying automatically.");
                    return;
                }

                var datagram = state.CreatePacket(
                    _sessionId,
                    UdpPacketType.ConnectivityCheck,
                    state.Challenge);

                foreach (var candidate in candidates)
                {
                    if (!IPAddress.TryParse(candidate.Address, out var address))
                        continue;
                    try
                    {
                        await socket.SendAsync(
                            datagram,
                            new IPEndPoint(address, candidate.Port),
                            cancellationToken).ConfigureAwait(false);
                    }
                    catch (SocketException)
                    {
                        // Continue trying the remaining candidates.
                    }
                }

                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }

            if (state.GetActiveEndpoint(PeerTimeout) is null)
                RaisePeerConnectionFailed(peerId, "No authenticated UDP response was received from the peer's candidates. Check NAT/firewall restrictions; ListenTogether will retry automatically.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal teardown.
        }
        catch (ObjectDisposedException)
        {
            // The participant left while checks were in flight.
        }
        finally
        {
            state.FinishChecks();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var socket = _candidates.LocalCandidateSet!.TransportSocket;
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await socket.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            foreach (var pair in _peers)
            {
                if (!pair.Value.TryDecrypt(received.Buffer, _sessionId, out var packet) ||
                    packet is null)
                    continue;

                ProcessAuthenticatedPacket(pair.Key, pair.Value, received.RemoteEndPoint, packet);
                break;
            }
        }
    }

    private void ProcessAuthenticatedPacket(
        string peerId,
        PeerState state,
        IPEndPoint remoteEndpoint,
        UdpSecurePacket packet)
    {
        switch (packet.Type)
        {
            case UdpPacketType.ConnectivityCheck:
                if (packet.Payload.Length != ConnectivityChallengeLength)
                    return;
                SetPeerConnected(peerId, state, remoteEndpoint);
                _ = SendControlPacketAsync(
                    state, remoteEndpoint, UdpPacketType.ConnectivityCheckResponse, packet.Payload);
                break;

            case UdpPacketType.ConnectivityCheckResponse:
                if (packet.Payload.Length == ConnectivityChallengeLength &&
                    CryptographicOperations.FixedTimeEquals(packet.Payload, state.Challenge))
                    SetPeerConnected(peerId, state, remoteEndpoint);
                break;

            case UdpPacketType.KeepAlive:
                ProcessKeepAlive(peerId, state, remoteEndpoint, packet.Payload);
                break;

            case UdpPacketType.Audio:
                if (packet.Payload.Length <= sizeof(ulong) ||
                    !state.IsConnectedFrom(remoteEndpoint, PeerTimeout))
                    return;
                var audioSequence = BinaryPrimitives.ReadUInt64BigEndian(packet.Payload);
                if (audioSequence == 0)
                    return;
                var opusPayload = packet.Payload.AsSpan(sizeof(ulong)).ToArray();
                RaiseAudioPacketReceived(peerId, opusPayload, audioSequence);
                break;
        }
    }

    private void SetPeerConnected(string peerId, PeerState state, IPEndPoint endpoint)
    {
        if (!state.TrySetConnectedEndpoint(endpoint))
            return;

        RaisePeerConnected(peerId, endpoint);
    }

    private async Task SendControlPacketAsync(
        PeerState state,
        IPEndPoint endpoint,
        UdpPacketType type,
        ReadOnlyMemory<byte> payload)
    {
        try
        {
            var socket = _candidates.LocalCandidateSet?.TransportSocket;
            if (socket is null || Volatile.Read(ref _disposed) == 1)
                return;
            var datagram = state.CreatePacket(_sessionId, type, payload.ToArray());
            await socket.SendAsync(datagram, endpoint, _cancellation?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is SocketException or ObjectDisposedException or OperationCanceledException)
        {
            // A failed response is recovered by the peer's next connectivity check.
        }
    }

    private void ProcessKeepAlive(
        string peerId,
        PeerState state,
        IPEndPoint endpoint,
        byte[] payload)
    {
        if (payload.Length != 9)
            return;

        if (payload[0] == 0)
        {
            SetPeerConnected(peerId, state, endpoint);
            var response = payload.ToArray();
            response[0] = 1;
            _ = SendControlPacketAsync(state, endpoint, UdpPacketType.KeepAlive, response);
        }
        else if (payload[0] == 1 && state.IsConnectedFrom(endpoint, PeerTimeout))
        {
            state.MarkReceived();
        }
    }

    private async Task KeepAliveLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(KeepAliveIntervalMilliseconds));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var socket = _candidates.LocalCandidateSet?.TransportSocket;
                if (socket is null)
                    return;

                foreach (var pair in _peers)
                {
                    var endpoint = pair.Value.GetActiveEndpoint(PeerTimeout);
                    if (endpoint is null)
                    {
                        StartConnectivityChecks(pair.Key, pair.Value);
                        continue;
                    }

                    var payload = new byte[9];
                    RandomNumberGenerator.Fill(payload.AsSpan(1));
                    try
                    {
                        var datagram = pair.Value.CreatePacket(
                            _sessionId, UdpPacketType.KeepAlive, payload);
                        await socket.SendAsync(datagram, endpoint, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        exception is SocketException or ObjectDisposedException)
                    {
                        pair.Value.ClearEndpoint();
                        StartConnectivityChecks(pair.Key, pair.Value);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal teardown.
        }
    }

    private void RaisePeerConnected(string peerId, IPEndPoint endpoint)
    {
        var handlers = PeerConnected;
        if (handlers is null)
            return;
        foreach (Action<string, IPEndPoint> handler in handlers.GetInvocationList())
        {
            try { handler(peerId, endpoint); }
            catch (Exception) { }
        }
    }

    private void RaiseAudioPacketReceived(string peerId, byte[] payload, ulong sequence)
    {
        var handlers = AudioPacketReceived;
        if (handlers is null)
            return;
        foreach (Action<string, byte[], ulong> handler in handlers.GetInvocationList())
        {
            try { handler(peerId, payload, sequence); }
            catch (Exception) { }
        }
    }

    private void RaisePeerConnectionAttemptStarted(string peerId)
    {
        var handlers = PeerConnectionAttemptStarted;
        if (handlers is null) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(peerId); } catch (Exception) { }
        }
    }

    private void RaisePeerConnectionFailed(string peerId, string reason)
    {
        var handlers = PeerConnectionFailed;
        if (handlers is null) return;
        foreach (Action<string, string> handler in handlers.GetInvocationList())
        {
            try { handler(peerId, reason); } catch (Exception) { }
        }
    }
    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _candidates.RemoteCandidatesUpdated -= OnRemoteCandidatesUpdated;
        _candidates.RemotePeerLeft -= OnRemotePeerLeft;
        _keyNegotiation.PeerSecured -= OnPeerSecured;

        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync().ConfigureAwait(false);
            var tasks = new[] { _receiveTask, _keepAliveTask }
                .Where(task => task is not null)
                .Cast<Task>()
                .ToArray();
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or SocketException) { }
            _cancellation.Dispose();
            _cancellation = null;
        }

        foreach (var pair in _peers)
            pair.Value.Dispose();
        _peers.Clear();
        GC.SuppressFinalize(this);
    }

    private sealed class PeerState : IDisposable
    {
        private readonly object _sync = new();
        private long _sendSequence;
        private IPEndPoint? _connectedEndpoint;
        private DateTimeOffset _lastReceivedUtc;
        private int _checking;
        private bool _disposed;

        public PeerState(PeerKeyAgreementResult result)
        {
            SendCodec = new UdpSecurePacketCodec(result.SendKey, result.SendNoncePrefix);
            ReceiveCodec = new UdpSecurePacketCodec(result.ReceiveKey, result.ReceiveNoncePrefix);
            Challenge = RandomNumberGenerator.GetBytes(ConnectivityChallengeLength);
        }

        public UdpSecurePacketCodec SendCodec { get; }
        public UdpSecurePacketCodec ReceiveCodec { get; }
        public byte[] Challenge { get; }

        public byte[] CreatePacket(Guid sessionId, UdpPacketType type, ReadOnlySpan<byte> payload)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var sequence = (ulong)Interlocked.Increment(ref _sendSequence);
                return SendCodec.Encrypt(sessionId, type, sequence, payload);
            }
        }

        public bool TryDecrypt(
            ReadOnlySpan<byte> datagram,
            Guid sessionId,
            out UdpSecurePacket? packet)
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    packet = null;
                    return false;
                }
                return ReceiveCodec.TryDecrypt(datagram, sessionId, out packet);
            }
        }

        public bool TryStartChecks() => Interlocked.CompareExchange(ref _checking, 1, 0) == 0;
        public void FinishChecks() => Interlocked.Exchange(ref _checking, 0);

        public bool TrySetConnectedEndpoint(IPEndPoint endpoint)
        {
            lock (_sync)
            {
                if (_disposed)
                    return false;
                var isNewEndpoint = _connectedEndpoint is null ||
                    !_connectedEndpoint.Equals(endpoint);
                _connectedEndpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
                _lastReceivedUtc = DateTimeOffset.UtcNow;
                return isNewEndpoint;
            }
        }

        public void MarkReceived()
        {
            lock (_sync)
                _lastReceivedUtc = DateTimeOffset.UtcNow;
        }

        public IPEndPoint? GetActiveEndpoint(TimeSpan timeout)
        {
            lock (_sync)
            {
                if (_connectedEndpoint is null)
                    return null;
                if (DateTimeOffset.UtcNow - _lastReceivedUtc > timeout)
                {
                    _connectedEndpoint = null;
                    return null;
                }
                return new IPEndPoint(_connectedEndpoint.Address, _connectedEndpoint.Port);
            }
        }

        public bool IsConnectedFrom(IPEndPoint endpoint, TimeSpan timeout)
        {
            lock (_sync)
            {
                if (_connectedEndpoint is null ||
                    !_connectedEndpoint.Equals(endpoint) ||
                    DateTimeOffset.UtcNow - _lastReceivedUtc > timeout)
                    return false;
                _lastReceivedUtc = DateTimeOffset.UtcNow;
                return true;
            }
        }

        public void ClearEndpoint()
        {
            lock (_sync)
                _connectedEndpoint = null;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                    return;
                _disposed = true;
                _connectedEndpoint = null;
                CryptographicOperations.ZeroMemory(Challenge);
                SendCodec.Dispose();
                ReceiveCodec.Dispose();
            }
        }
    }
}
