using System.Net.Http;
using ListenTogether.Core.Sessions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// SignalR connection to the ListenTogether signalling and relay service.
/// SignalR currently carries session events and relayed Opus packets; peer
/// candidate exchange prepares sessions for direct UDP transport.
/// </summary>
public sealed class SignalRSessionClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private string? _sessionCode;
    private Guid _sessionId;
    private bool _isHost;
    private bool _isDisposed;

    public SignalRSessionClient(Uri serverUri)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        if (!serverUri.IsAbsoluteUri || serverUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The relay address must be an absolute HTTP or HTTPS URL.", nameof(serverUri));

        var hubUri = new Uri(serverUri, "/hubs/listening");
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options => options.Transports = HttpTransportType.WebSockets)
            .Build();

        _connection.On<byte[], long>("ReceiveAudioPacket", (packet, sequence) =>
            AudioPacketReceived?.Invoke(packet, sequence));
        _connection.On<SessionParticipant>("ParticipantJoined", participant =>
            ParticipantJoined?.Invoke(participant));
        _connection.On<SessionParticipant>("ParticipantLeft", participant =>
            ParticipantLeft?.Invoke(participant));
        _connection.On<PeerCandidateMessage>("PeerCandidateReceived", message =>
            PeerCandidateReceived?.Invoke(message));
        _connection.On<PeerKeyOfferMessage>("PeerKeyOfferReceived", message =>
            PeerKeyOfferReceived?.Invoke(message));
        _connection.On<PeerKeyProofMessage>("PeerKeyProofReceived", message =>
            PeerKeyProofReceived?.Invoke(message));
        _connection.On<string>("SessionEnded", reason =>
        {
            _sessionCode = null;
            _sessionId = Guid.Empty;
            ConnectionId = null;
            _isHost = false;
            SessionEnded?.Invoke(reason);
        });
        _connection.Closed += exception =>
        {
            _sessionCode = null;
            _sessionId = Guid.Empty;
            ConnectionId = null;
            _isHost = false;
            ConnectionClosed?.Invoke(exception);
            return Task.CompletedTask;
        };
    }

    public event Action<byte[], long>? AudioPacketReceived;
    public event Action<SessionParticipant>? ParticipantJoined;
    public event Action<SessionParticipant>? ParticipantLeft;
    public event Action<PeerCandidateMessage>? PeerCandidateReceived;
    public event Action<PeerKeyOfferMessage>? PeerKeyOfferReceived;
    public event Action<PeerKeyProofMessage>? PeerKeyProofReceived;
    public event Action<SessionJoinResult>? SessionJoined;
    public event Action<string>? SessionEnded;
    public event Action<Exception?>? ConnectionClosed;

    public bool IsConnected => _connection.State == HubConnectionState.Connected;
    public bool IsHost => _isHost;
    public string? SessionCode => _sessionCode;
    public Guid SessionId => _sessionId;
    public string? ConnectionId { get; private set; }

    public async Task<SessionInvitation> CreateSessionAsync(
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var invitation = await _connection.InvokeAsync<SessionInvitation>(
            "CreateSession",
            displayName,
            cancellationToken).ConfigureAwait(false);

        _sessionCode = invitation.Code;
        _sessionId = invitation.SessionId;
        ConnectionId = await _connection.InvokeAsync<string>("GetConnectionId", cancellationToken).ConfigureAwait(false);
        _isHost = true;
        return invitation;
    }

    public async Task<SessionJoinResult> JoinSessionAsync(
        string code,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var result = await _connection.InvokeAsync<SessionJoinResult>(
            "JoinSession",
            code,
            displayName,
            cancellationToken).ConfigureAwait(false);

        _sessionCode = result.Invitation.Code;
        _sessionId = result.Invitation.SessionId;
        ConnectionId = await _connection.InvokeAsync<string>("GetConnectionId", cancellationToken).ConfigureAwait(false);
        _isHost = false;
        SessionJoined?.Invoke(result);
        return result;
    }

    /// <summary>Publishes a validated UDP candidate to the other session participants.</summary>
    public Task PublishPeerCandidateAsync(
        PeerCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsConnected || string.IsNullOrEmpty(_sessionCode))
            throw new InvalidOperationException("Join a connected session before publishing peer candidates.");
        ArgumentNullException.ThrowIfNull(candidate);
        if (!candidate.IsValid)
            throw new ArgumentException("The peer candidate is invalid.", nameof(candidate));

        return _connection.InvokeAsync("PublishPeerCandidate", candidate, cancellationToken);
    }

    /// <summary>Publishes this participant's ECDH offer to one validated session peer.</summary>
    public Task PublishPeerKeyOfferAsync(
        PeerKeyOffer offer,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsConnected || _sessionId == Guid.Empty || ConnectionId is null)
            throw new InvalidOperationException("Join a connected session before exchanging peer keys.");
        ArgumentNullException.ThrowIfNull(offer);
        if (offer.SessionId != _sessionId ||
            !string.Equals(offer.FromConnectionId, ConnectionId, StringComparison.Ordinal) ||
            offer.PublicKey is null || offer.PublicKey.Length is < 64 or > 512 ||
            offer.Nonce is null || offer.Nonce.Length != 32)
            throw new ArgumentException("The peer key offer is not valid for this connection.", nameof(offer));

        return _connection.InvokeAsync("PublishPeerKeyOffer", offer, cancellationToken);
    }

    /// <summary>Publishes a transcript-bound key confirmation proof to the named peer.</summary>
    public Task PublishPeerKeyProofAsync(
        string toConnectionId,
        byte[] transcriptHash,
        byte[] proof,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsConnected || _sessionId == Guid.Empty || string.IsNullOrWhiteSpace(toConnectionId))
            throw new InvalidOperationException("Join a connected session before exchanging peer keys.");
        if (transcriptHash is null || transcriptHash.Length != 32 ||
            proof is null || proof.Length != 32)
            throw new ArgumentException("The key confirmation proof is invalid.");

        return _connection.InvokeAsync(
            "PublishPeerKeyProof", _sessionId, toConnectionId, transcriptHash, proof, cancellationToken);
    }

    public Task SendAudioPacketAsync(
        byte[] packet,
        long sequence,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsConnected || !_isHost || string.IsNullOrEmpty(_sessionCode))
            throw new InvalidOperationException("Only a connected session host can send audio.");

        if (packet is null || packet.Length is < 1 or > 1_275)
            throw new ArgumentOutOfRangeException(nameof(packet), "Opus packets must contain between 1 and 1,275 bytes.");

        return _connection.SendAsync(
            "SendAudioPacket",
            packet,
            sequence,
            cancellationToken);
    }

    public async Task LeaveSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !IsConnected || string.IsNullOrEmpty(_sessionCode))
            return;

        await _connection.InvokeAsync("LeaveSession", cancellationToken).ConfigureAwait(false);
        _sessionCode = null;
        _sessionId = Guid.Empty;
        ConnectionId = null;
        _isHost = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
            return;

        try { await LeaveSessionAsync().ConfigureAwait(false); }
        catch { /* Connection may already have closed. */ }

        _isDisposed = true;
        try { await _connection.StopAsync().ConfigureAwait(false); }
        finally { await _connection.DisposeAsync().ConfigureAwait(false); }

        GC.SuppressFinalize(this);
    }

    private async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_connection.State == HubConnectionState.Connected)
            return;

        if (_connection.State != HubConnectionState.Disconnected)
            throw new InvalidOperationException($"The relay connection is currently {_connection.State}.");

        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
    }
}

public sealed class SignalRSessionClientFactory
{
    public SignalRSessionClient Create(Uri serverUri) => new(serverUri);
}
