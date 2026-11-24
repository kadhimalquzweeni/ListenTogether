using System.Collections.Concurrent;
using System.Security.Cryptography;
using ListenTogether.Core.Sessions;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Coordinates one independent ECDH agreement per remote participant. A peer is
/// considered ready only after the remote transcript hash and HMAC proof verify.
/// </summary>
public sealed class PeerKeyNegotiationCoordinator : IDisposable
{
    private readonly IPeerKeySignalling _session;
    private readonly ConcurrentDictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private bool _disposed;

    public PeerKeyNegotiationCoordinator(IPeerKeySignalling session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.PeerKeyOfferReceived += OnPeerKeyOfferReceived;
        _session.PeerKeyProofReceived += OnPeerKeyProofReceived;
        _session.ParticipantLeft += OnParticipantLeft;
        _session.ParticipantJoined += OnParticipantJoined;
        _session.SessionJoined += OnSessionJoined;
        _session.SessionEnded += OnSessionEnded;
        _session.ConnectionClosed += OnConnectionClosed;
    }

    /// <summary>Raised only after both participants have confirmed the same transcript.</summary>
    public event Action<string, PeerKeyAgreementResult>? PeerSecured;

    public IReadOnlyCollection<string> SecuredPeers =>
        _peers.Where(pair => pair.Value.IsSecured).Select(pair => pair.Key).ToArray();

    /// <summary>Starts negotiation with a peer. Safe to call more than once.</summary>
    public async Task BeginPeerAsync(string remoteConnectionId, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var state = GetOrCreatePeer(remoteConnectionId);
        PeerKeyOffer? offer = null;

        lock (state.Sync)
        {
            EnsureAgreement(state, remoteConnectionId);
            if (!state.OfferPublished)
            {
                state.OfferPublished = true;
                offer = state.Agreement!.LocalOffer;
            }
        }

        if (offer is not null)
        {
            try
            {
                await _session.PublishPeerKeyOfferAsync(offer, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                lock (state.Sync)
                    state.OfferPublished = false;
                throw;
            }
        }
    }

    /// <summary>Starts negotiations with every other participant in the join result.</summary>
    public Task BeginSessionPeersAsync(SessionJoinResult joinResult, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(joinResult);
        var localId = _session.ConnectionId;
        if (string.IsNullOrWhiteSpace(localId))
            throw new InvalidOperationException("The SignalR connection identity is not available.");

        return BeginPeersAsync(
            joinResult.Participants
                .Where(participant => !string.IsNullOrWhiteSpace(participant.ConnectionId))
                .Select(participant => participant.ConnectionId!)
                .Where(connectionId => !string.Equals(connectionId, localId, StringComparison.Ordinal)),
            cancellationToken);
    }

    /// <summary>Starts independent negotiations with all supplied remote participants.</summary>
    public async Task BeginPeersAsync(
        IEnumerable<string> remoteConnectionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(remoteConnectionIds);
        foreach (var connectionId in remoteConnectionIds.Distinct(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await BeginPeerAsync(connectionId, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Returns a secured peer's directional key material, or null while negotiating.</summary>
    public PeerKeyAgreementResult? GetSecuredResult(string remoteConnectionId)
    {
        ThrowIfDisposed();
        if (!_peers.TryGetValue(remoteConnectionId, out var state))
            return null;
        lock (state.Sync)
            return state.IsSecured ? state.Result : null;
    }

    private void OnPeerKeyOfferReceived(PeerKeyOfferMessage message) =>
        _ = ProcessOfferAsync(message);

    private async Task ProcessOfferAsync(PeerKeyOfferMessage message)
    {
        try
        {
            ThrowIfDisposed();
            if (message is null || message.Offer is null ||
                !string.Equals(message.PeerConnectionId, message.Offer.FromConnectionId, StringComparison.Ordinal) ||
                !string.Equals(message.Offer.ToConnectionId, _session.ConnectionId, StringComparison.Ordinal) ||
                message.Offer.SessionId != _session.SessionId)
                return;

            var state = GetOrCreatePeer(message.PeerConnectionId);
            byte[]? proof = null;
            byte[]? transcriptHash = null;
            lock (state.Sync)
            {
                EnsureAgreement(state, message.PeerConnectionId);
                if (state.Result is null)
                {
                    state.Result = state.Agreement!.Complete(message.Offer);
                    proof = state.Result.LocalProof.ToArray();
                    transcriptHash = state.Result.TranscriptHash.ToArray();
                }
            }

            if (proof is not null && transcriptHash is not null)
            {
                try
                {
                    await _session.PublishPeerKeyProofAsync(
                        message.PeerConnectionId, transcriptHash, proof).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(proof);
                    CryptographicOperations.ZeroMemory(transcriptHash);
                }
            }

            TryCompletePeer(message.PeerConnectionId, state);
        }
        catch (Exception)
        {
            RemovePeer(message?.PeerConnectionId);
        }
    }

    private void OnPeerKeyProofReceived(PeerKeyProofMessage message)
    {
        try
        {
            ThrowIfDisposed();
            if (message is null ||
                message.SessionId != _session.SessionId ||
                !string.Equals(message.ToConnectionId, _session.ConnectionId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(message.FromConnectionId) ||
                message.TranscriptHash is null || message.TranscriptHash.Length != 32 ||
                message.Proof is null || message.Proof.Length != 32)
                return;

            var state = GetOrCreatePeer(message.FromConnectionId);

            lock (state.Sync)
            {
                if (state.Result is null)
                {
                    state.PendingProof = message;
                    return;
                }
            }

            VerifyAndComplete(message.FromConnectionId, state, message);
        }
        catch (Exception exception) when (exception is ObjectDisposedException or ArgumentException)
        {
            // Ignore malformed peer identities and session teardown races.
        }
    }

    private void VerifyAndComplete(string peerId, PeerState state, PeerKeyProofMessage message)
    {
        PeerKeyAgreementResult? result;
        var invalid = false;
        lock (state.Sync)
        {
            result = state.Result;
            if (result is null || state.IsSecured)
                return;

            if (!CryptographicOperations.FixedTimeEquals(result.TranscriptHash, message.TranscriptHash) ||
                !result.VerifyPeerProof(message.Proof))
            {
                invalid = true;
            }
            else
            {
                state.IsSecured = true;
            }
        }

        if (invalid)
        {
            RemovePeer(peerId);
            return;
        }

        // A UI or media subscriber must not break signalling callback processing.
        var handlers = PeerSecured;
        if (handlers is null)
            return;
        foreach (Action<string, PeerKeyAgreementResult> handler in handlers.GetInvocationList())
        {
            try { handler(peerId, result!); }
            catch (Exception) { /* Isolate subscriber failures from protocol state. */ }
        }
    }

    private void TryCompletePeer(string peerId, PeerState state)
    {
        PeerKeyProofMessage? pending;
        lock (state.Sync)
        {
            pending = state.PendingProof;
            state.PendingProof = null;
        }
        if (pending is not null)
            VerifyAndComplete(peerId, state, pending);
    }

    private void EnsureAgreement(PeerState state, string remoteConnectionId)
    {
        if (state.Agreement is not null)
            return;
        if (_session.SessionId == Guid.Empty || string.IsNullOrWhiteSpace(_session.ConnectionId))
            throw new InvalidOperationException("A joined session identity is required for peer negotiation.");
        state.Agreement = PeerKeyAgreement.Create(_session.SessionId, _session.ConnectionId, remoteConnectionId);
    }

    private PeerState GetOrCreatePeer(string remoteConnectionId)
    {
        if (string.IsNullOrWhiteSpace(remoteConnectionId) ||
            string.Equals(remoteConnectionId, _session.ConnectionId, StringComparison.Ordinal))
            throw new ArgumentException("A different peer connection ID is required.", nameof(remoteConnectionId));
        return _peers.GetOrAdd(remoteConnectionId, _ => new PeerState());
    }

    private void OnSessionJoined(SessionJoinResult result) =>
        _ = BeginSessionPeersSafelyAsync(result);

    private async Task BeginSessionPeersSafelyAsync(SessionJoinResult result)
    {
        try { await BeginSessionPeersAsync(result).ConfigureAwait(false); }
        catch (Exception) { ClearPeers(); }
    }

    private void OnParticipantJoined(SessionParticipant participant)
    {
        if (!string.IsNullOrWhiteSpace(participant.ConnectionId))
            _ = BeginPeerSafelyAsync(participant.ConnectionId);
    }

    private async Task BeginPeerSafelyAsync(string connectionId)
    {
        try { await BeginPeerAsync(connectionId).ConfigureAwait(false); }
        catch (Exception) { RemovePeer(connectionId); }
    }

    private void OnParticipantLeft(SessionParticipant participant)
    {
        // Connection IDs are supplied by the hub's peer departure notification.
        if (!string.IsNullOrWhiteSpace(participant.ConnectionId))
            RemovePeer(participant.ConnectionId);
    }

    private void OnSessionEnded(string _) => ClearPeers();
    private void OnConnectionClosed(Exception? _) => ClearPeers();

    private void RemovePeer(string? peerId)
    {
        if (peerId is null || !_peers.TryRemove(peerId, out var state))
            return;
        state.Dispose();
    }

    private void ClearPeers()
    {
        foreach (var peerId in _peers.Keys)
            RemovePeer(peerId);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _session.PeerKeyOfferReceived -= OnPeerKeyOfferReceived;
        _session.PeerKeyProofReceived -= OnPeerKeyProofReceived;
        _session.ParticipantLeft -= OnParticipantLeft;
        _session.ParticipantJoined -= OnParticipantJoined;
        _session.SessionJoined -= OnSessionJoined;
        _session.SessionEnded -= OnSessionEnded;
        _session.ConnectionClosed -= OnConnectionClosed;
        ClearPeers();
    }

    private sealed class PeerState : IDisposable
    {
        public object Sync { get; } = new();
        public PeerKeyAgreement? Agreement { get; set; }
        public PeerKeyAgreementResult? Result { get; set; }
        public PeerKeyProofMessage? PendingProof { get; set; }
        public bool OfferPublished { get; set; }
        public bool IsSecured { get; set; }

        public void Dispose()
        {
            lock (Sync)
            {
                Agreement?.Dispose();
                Result?.Dispose();
                Agreement = null;
                Result = null;
                PendingProof = null;
                IsSecured = false;
            }
        }
    }
}
