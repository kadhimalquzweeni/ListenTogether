using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ListenTogether.Core.Sessions;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Gathers this participant's UDP endpoints and exchanges them through the
/// authenticated session signalling channel. It deliberately does not switch
/// media transport; SignalR remains the media fallback until connectivity checks
/// and the encrypted UDP media pipeline are complete.
/// </summary>
public sealed class UdpPeerCandidateCoordinator : IDisposable
{
    public const int DefaultStunPort = 3478;

    private readonly IPeerKeySignalling _signalling;
    private readonly IUdpCandidateGatherer _gatherer;
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PeerCandidate>> _remoteCandidates =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private UdpCandidateSet? _localCandidateSet;
    private int _started;
    private int _disposed;

    public UdpPeerCandidateCoordinator(
        IPeerKeySignalling signalling,
        IUdpCandidateGatherer? gatherer = null)
    {
        _signalling = signalling ?? throw new ArgumentNullException(nameof(signalling));
        _gatherer = gatherer ?? new UdpCandidateGatherer();

        _signalling.PeerCandidateReceived += OnPeerCandidateReceived;
        _signalling.ParticipantJoined += OnParticipantJoined;
        _signalling.ParticipantLeft += OnParticipantLeft;
        _signalling.SessionEnded += OnSessionEnded;
        _signalling.ConnectionClosed += OnConnectionClosed;
    }

    /// <summary>Raised when a remote participant's candidate set changes.</summary>
    public event Action<string, IReadOnlyList<PeerCandidate>>? RemoteCandidatesUpdated;

    public IReadOnlyList<PeerCandidate> LocalCandidates =>
        _localCandidateSet?.Candidates ?? Array.Empty<PeerCandidate>();

    /// <summary>
    /// Exposes the same bound socket used for STUN gathering. A future direct
    /// transport must keep this candidate set alive and reuse this socket.
    /// </summary>
    public UdpCandidateSet? LocalCandidateSet => _localCandidateSet;

    public IReadOnlyList<PeerCandidate> GetRemoteCandidates(string peerConnectionId)
    {
        if (string.IsNullOrWhiteSpace(peerConnectionId))
            return Array.Empty<PeerCandidate>();

        return _remoteCandidates.TryGetValue(peerConnectionId, out var candidates)
            ? candidates.Values.ToArray()
            : Array.Empty<PeerCandidate>();
    }

    public async Task StartAsync(
        Uri signallingServerUri,
        int stunPort = DefaultStunPort,
        TimeSpan? stunTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(signallingServerUri);
        if (!signallingServerUri.IsAbsoluteUri ||
            signallingServerUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The signalling server URI must be absolute HTTP or HTTPS.", nameof(signallingServerUri));
        if (stunPort is <= 0 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(stunPort));
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            throw new InvalidOperationException("UDP candidate gathering has already been started.");

        try
        {
            var stunServer = await ResolveStunEndpointAsync(
                signallingServerUri.DnsSafeHost, stunPort, cancellationToken).ConfigureAwait(false);
            var candidateSet = await _gatherer.GatherAsync(
                stunServer, stunTimeout ?? TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);

            if (Volatile.Read(ref _disposed) == 1)
            {
                candidateSet.Dispose();
                throw new ObjectDisposedException(nameof(UdpPeerCandidateCoordinator));
            }

            _localCandidateSet = candidateSet;
            await PublishLocalCandidatesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _localCandidateSet?.Dispose();
            _localCandidateSet = null;
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    private static async Task<IPEndPoint> ResolveStunEndpointAsync(
        string host,
        int port,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        var address = addresses.FirstOrDefault(candidate =>
            candidate.AddressFamily == AddressFamily.InterNetwork);
        if (address is null)
            throw new NotSupportedException("The configured STUN host did not resolve to an IPv4 address.");

        return new IPEndPoint(address, port);
    }

    private async Task PublishLocalCandidatesAsync(CancellationToken cancellationToken = default)
    {
        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var candidates = _localCandidateSet?.Candidates;
            if (candidates is null || Volatile.Read(ref _disposed) == 1)
                return;

            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await _signalling.PublishPeerCandidateAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _publishLock.Release();
        }
    }

    private void OnPeerCandidateReceived(PeerCandidateMessage message)
    {
        if (Volatile.Read(ref _disposed) == 1 ||
            message is null ||
            string.IsNullOrWhiteSpace(message.PeerConnectionId) ||
            string.Equals(message.PeerConnectionId, _signalling.ConnectionId, StringComparison.Ordinal) ||
            message.Candidate is null ||
            !message.Candidate.IsValid)
            return;

        var candidates = _remoteCandidates.GetOrAdd(
            message.PeerConnectionId,
            _ => new ConcurrentDictionary<string, PeerCandidate>(StringComparer.Ordinal));
        var key = $"{message.Candidate.Type}|{message.Candidate.Address}|{message.Candidate.Port}";
        if (!candidates.TryAdd(key, message.Candidate))
            return;

        RaiseRemoteCandidatesUpdated(message.PeerConnectionId, candidates.Values.ToArray());
    }

    private void OnParticipantJoined(SessionParticipant participant)
    {
        if (Volatile.Read(ref _disposed) == 1 ||
            string.IsNullOrWhiteSpace(participant.ConnectionId) ||
            string.Equals(participant.ConnectionId, _signalling.ConnectionId, StringComparison.Ordinal))
            return;

        _ = RepublishCandidatesSafelyAsync();
    }

    private async Task RepublishCandidatesSafelyAsync()
    {
        try { await PublishLocalCandidatesAsync().ConfigureAwait(false); }
        catch (Exception) { /* Candidate exchange is optional; SignalR media remains available. */ }
    }

    private void OnParticipantLeft(SessionParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.ConnectionId))
            _remoteCandidates.TryRemove(participant.ConnectionId, out _);
    }

    private void OnSessionEnded(string _) => _remoteCandidates.Clear();
    private void OnConnectionClosed(Exception? _) => _remoteCandidates.Clear();

    private void RaiseRemoteCandidatesUpdated(string peerId, IReadOnlyList<PeerCandidate> candidates)
    {
        var handlers = RemoteCandidatesUpdated;
        if (handlers is null)
            return;

        foreach (Action<string, IReadOnlyList<PeerCandidate>> handler in handlers.GetInvocationList())
        {
            try { handler(peerId, candidates); }
            catch (Exception) { /* Isolate consumer failures from signalling callbacks. */ }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, this);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _signalling.PeerCandidateReceived -= OnPeerCandidateReceived;
        _signalling.ParticipantJoined -= OnParticipantJoined;
        _signalling.ParticipantLeft -= OnParticipantLeft;
        _signalling.SessionEnded -= OnSessionEnded;
        _signalling.ConnectionClosed -= OnConnectionClosed;

        _remoteCandidates.Clear();
        Interlocked.Exchange(ref _localCandidateSet, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
