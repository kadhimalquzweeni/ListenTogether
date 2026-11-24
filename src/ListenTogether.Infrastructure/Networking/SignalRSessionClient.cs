using System.Net.Http;
using ListenTogether.Core.Sessions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;

namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// SignalR connection to the ListenTogether relay. A session invite code is the
/// bearer credential; the relay forwards Opus packets and does not persist audio.
/// </summary>
public sealed class SignalRSessionClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private string? _sessionCode;
    private bool _isHost;
    private bool _isDisposed;

    public SignalRSessionClient(Uri serverUri)
    {
        ArgumentNullException.ThrowIfNull(serverUri);
        if (!serverUri.IsAbsoluteUri || serverUri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The relay address must be an absolute HTTP or HTTPS URL.", nameof(serverUri));

        var hubUri = new Uri(serverUri, "/hubs/listening");
        _connection = new HubConnectionBuilder()
            .WithUrl(hubUri, options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .Build();

        _connection.On<byte[], long>("ReceiveAudioPacket", (packet, sequence) =>
            AudioPacketReceived?.Invoke(packet, sequence));
        _connection.On<SessionParticipant>("ParticipantJoined", participant =>
            ParticipantJoined?.Invoke(participant));
        _connection.On<SessionParticipant>("ParticipantLeft", participant =>
            ParticipantLeft?.Invoke(participant));
        _connection.On<string>("SessionEnded", reason =>
        {
            _sessionCode = null;
            _isHost = false;
            SessionEnded?.Invoke(reason);
        });
        _connection.Closed += exception =>
        {
            _sessionCode = null;
            _isHost = false;
            ConnectionClosed?.Invoke(exception);
            return Task.CompletedTask;
        };
    }

    public event Action<byte[], long>? AudioPacketReceived;
    public event Action<SessionParticipant>? ParticipantJoined;
    public event Action<SessionParticipant>? ParticipantLeft;
    public event Action<string>? SessionEnded;
    public event Action<Exception?>? ConnectionClosed;

    public bool IsConnected => _connection.State == HubConnectionState.Connected;
    public bool IsHost => _isHost;
    public string? SessionCode => _sessionCode;

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
        _isHost = false;
        return result;
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

    /// <summary>Sends up to five 20 ms Opus frames in one WebSocket message.</summary>
    public Task SendAudioPacketBatchAsync(
        IReadOnlyList<byte[]> packets,
        IReadOnlyList<long> sequences,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!IsConnected || !_isHost || string.IsNullOrEmpty(_sessionCode))
            throw new InvalidOperationException("Only a connected session host can send audio.");
        if (packets is null || sequences is null ||
            packets.Count is < 1 or > 5 || packets.Count != sequences.Count)
            throw new ArgumentException("Audio batches must contain between one and five matching packets and sequences.");

        for (var index = 0; index < packets.Count; index++)
        {
            if (packets[index] is null || packets[index].Length is < 1 or > 1_275)
                throw new ArgumentOutOfRangeException(nameof(packets), "Opus packets must contain between 1 and 1,275 bytes.");
            if (sequences[index] < 0 || (index > 0 && sequences[index] <= sequences[index - 1]))
                throw new ArgumentOutOfRangeException(nameof(sequences), "Packet sequences must be non-negative and strictly increasing.");
        }

        return _connection.SendAsync("SendAudioPacketBatch", packets.ToArray(), sequences.ToArray(), cancellationToken);
    }

    public async Task LeaveSessionAsync(CancellationToken cancellationToken = default)
    {
        if (_isDisposed || !IsConnected || string.IsNullOrEmpty(_sessionCode))
            return;

        await _connection.InvokeAsync("LeaveSession", cancellationToken).ConfigureAwait(false);
        _sessionCode = null;
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
