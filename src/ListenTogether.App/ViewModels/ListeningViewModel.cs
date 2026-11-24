using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using ListenTogether.Core.Sessions;
using ListenTogether.Infrastructure.Audio;
using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.App.ViewModels;

public sealed class ListeningViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly SignalRSessionClient _client;
    private readonly PeerKeyNegotiationCoordinator _peerNegotiation;
    private readonly UdpPeerCandidateCoordinator _peerCandidates;
    private UdpPeerTransportCoordinator? _udpTransport;
    private readonly OpusAudioDecoder _decoder = new();
    private WasapiAudioPlayback? _playback;
    private int _isDisposed;
    private readonly object _decodeLock = new();
    private readonly UdpAudioJitterBuffer _jitterBuffer = new();
    private CancellationTokenSource? _jitterCancellation;
    private Task? _jitterTask;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly string _sessionCode;
    private readonly Uri _serverUri;
    private bool _isConnecting;
    private bool _isConnected;
    private string _statusMessage = "Connecting to the listening session…";
    private string _hostDisplayName = "—";
    private int _participantCount;

    public ListeningViewModel(Uri serverUri, string displayName, string sessionCode)
    {
        _sessionCode = sessionCode;
        _serverUri = serverUri;
        _client = new SignalRSessionClient(serverUri);
        _peerNegotiation = new PeerKeyNegotiationCoordinator(_client);
        _peerNegotiation.PeerSecured += OnPeerSecured;
        _peerCandidates = new UdpPeerCandidateCoordinator(_client);
        _client.ParticipantJoined += OnParticipantJoined;
        _client.ParticipantLeft += OnParticipantLeft;
        _client.SessionEnded += OnSessionEnded;
        _client.ConnectionClosed += OnConnectionClosed;

        LeaveCommand = new AsyncRelayCommand(LeaveAsync, () => IsConnected || IsConnecting);
        _displayName = displayName;
    }

    private readonly string _displayName;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string HostDisplayName
    {
        get => _hostDisplayName;
        private set => SetProperty(ref _hostDisplayName, value);
    }

    public int ParticipantCount
    {
        get => _participantCount;
        private set => SetProperty(ref _participantCount, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (SetProperty(ref _isConnecting, value))
                (LeaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (SetProperty(ref _isConnected, value))
                (LeaveCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    public ICommand LeaveCommand { get; }

    public async Task ConnectAsync()
    {
        if (Volatile.Read(ref _isDisposed) == 1)
            return;
        try { await _lifecycle.WaitAsync(); }
        catch (ObjectDisposedException) { return; }

        try
        {
            if (Volatile.Read(ref _isDisposed) == 1 || IsConnecting || IsConnected)
                return;

            IsConnecting = true;
            StatusMessage = "Joining session…";
            try
            {
                _playback = new WasapiAudioPlayback();
                var result = await _client.JoinSessionAsync(_sessionCode, _displayName);
                if (Volatile.Read(ref _isDisposed) == 1)
                    return;
                if (_client.SessionCode is null)
                {
                    StatusMessage = "The host ended the session before you could join.";
                    return;
                }

                StartJitterBuffer();

                try
                {
                    await _peerCandidates.StartAsync(_serverUri, stunTimeout: TimeSpan.FromSeconds(2));
                    _udpTransport = new UdpPeerTransportCoordinator(_peerCandidates, _peerNegotiation, _client.SessionId);
                    _udpTransport.PeerConnected += OnDirectPeerConnected;
                    _udpTransport.PeerConnectionAttemptStarted += OnPeerConnectionAttemptStarted;
                    _udpTransport.PeerConnectionFailed += OnPeerConnectionFailed;
                    _udpTransport.AudioPacketReceived += OnDirectAudioPacketReceived;
                    await _udpTransport.StartAsync();
                }
                catch (Exception exception)
                {
                    if (_udpTransport is not null)
                    {
                        await _udpTransport.DisposeAsync();
                        _udpTransport = null;
                    }
                    _peerCandidates.Dispose();
                    await _client.LeaveSessionAsync();
                    if (_playback is not null)
                    {
                        await _playback.DisposeAsync();
                        _playback = null;
                    }
                    StatusMessage = $"Couldn't establish direct UDP transport: {exception.Message}. Audio is never relayed through the server.";
                    return;
                }

                HostDisplayName = result.HostDisplayName;
                ParticipantCount = result.Participants.Count;
                IsConnected = true;
                _playback.Start();
                StatusMessage = $"You're listening with {HostDisplayName}. Establishing a secure direct UDP audio connection…";
            }
            catch (Exception exception)
            {
                StatusMessage = $"Couldn't join the session: {exception.Message}. ListenTogether requires direct UDP; server audio relay is disabled.";
            }
            finally
            {
                IsConnecting = false;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task LeaveAsync()
    {
        if (Volatile.Read(ref _isDisposed) == 1)
            return;
        try { await _lifecycle.WaitAsync(); }
        catch (ObjectDisposedException) { return; }

        try { await LeaveCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task LeaveCoreAsync()
    {
        if (!IsConnected && !_client.IsConnected)
            return;

        StatusMessage = "Leaving session…";
        IsConnected = false;

        await StopJitterBufferAsync();

        if (_udpTransport is not null)
        {
            _udpTransport.PeerConnected -= OnDirectPeerConnected;
            _udpTransport.PeerConnectionAttemptStarted -= OnPeerConnectionAttemptStarted;
            _udpTransport.PeerConnectionFailed -= OnPeerConnectionFailed;
            _udpTransport.AudioPacketReceived -= OnDirectAudioPacketReceived;
            await _udpTransport.DisposeAsync();
            _udpTransport = null;
        }

        _peerNegotiation.Dispose();
        _peerCandidates.Dispose();
        try { await _client.LeaveSessionAsync(); }
        catch (Exception exception) { StatusMessage = $"Couldn't leave cleanly: {exception.Message}"; }

        _playback?.Clear();
        ParticipantCount = 0;
        StatusMessage = "You've left the listening session.";
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        await _lifecycle.WaitAsync();
        try
        {
            _peerNegotiation.PeerSecured -= OnPeerSecured;
            _client.ParticipantJoined -= OnParticipantJoined;
            _client.ParticipantLeft -= OnParticipantLeft;
            _client.SessionEnded -= OnSessionEnded;
            _client.ConnectionClosed -= OnConnectionClosed;

            await LeaveCoreAsync();
            await StopJitterBufferAsync();
            lock (_decodeLock)
            {
                _decoder.Dispose();
                _playback?.Clear();
            }
            if (_playback is not null)
            {
                await _playback.DisposeAsync();
                _playback = null;
            }
            if (_udpTransport is not null)
            {
                _udpTransport.PeerConnected -= OnDirectPeerConnected;
                _udpTransport.AudioPacketReceived -= OnDirectAudioPacketReceived;
                await _udpTransport.DisposeAsync();
                _udpTransport = null;
            }
            _peerNegotiation.Dispose();
            _peerCandidates.Dispose();
            await _client.DisposeAsync();
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private void OnPeerSecured(string peerConnectionId, PeerKeyAgreementResult result)
        => NotifyOnUi(() => StatusMessage = $"A secure peer link has been established; direct UDP connectivity is being checked.");

    private void OnDirectPeerConnected(string peerConnectionId, System.Net.IPEndPoint endpoint)
        => NotifyOnUi(() => StatusMessage = $"Secure direct UDP audio connection established with peer {peerConnectionId}.");

    private void OnPeerConnectionAttemptStarted(string peerConnectionId)
        => NotifyOnUi(() => StatusMessage = $"Negotiating direct UDP with peer {peerConnectionId}…");

    private void OnPeerConnectionFailed(string peerConnectionId, string reason)
        => NotifyOnUi(() => StatusMessage = $"Direct UDP connection to peer {peerConnectionId} is not established. {reason}");

    private void OnDirectAudioPacketReceived(string peerConnectionId, byte[] packet, ulong sequence)
        => _jitterBuffer.TryEnqueue(sequence, packet);

    private void StartJitterBuffer()
    {
        _jitterBuffer.Clear();
        _jitterCancellation = new CancellationTokenSource();
        _jitterTask = DrainJitterBufferAsync(_jitterCancellation.Token);
    }

    private async Task DrainJitterBufferAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(10));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var frame in _jitterBuffer.DrainReady())
                    PlayAudioPacket(frame.Payload);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal session teardown.
        }
    }

    private async Task StopJitterBufferAsync()
    {
        var cancellation = _jitterCancellation;
        _jitterCancellation = null;
        if (cancellation is not null)
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            if (_jitterTask is not null)
            {
                try { await _jitterTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            cancellation.Dispose();
        }

        _jitterTask = null;
        _jitterBuffer.Clear();
    }

    private void PlayAudioPacket(byte[] packet)
    {
        try
        {
            lock (_decodeLock)
            {
                var frame = _decoder.Decode(packet);
                _playback?.Write(frame);
            }
        }
        catch (Exception exception)
        {
            NotifyOnUi(() => StatusMessage = $"Audio playback warning: {exception.Message}");
        }
    }

    private void OnParticipantJoined(SessionParticipant participant)
        => NotifyOnUi(() =>
        {
            ParticipantCount++;
            StatusMessage = $"{participant.DisplayName} joined the session.";
        });

    private void OnParticipantLeft(SessionParticipant participant)
        => NotifyOnUi(() =>
        {
            ParticipantCount = Math.Max(1, ParticipantCount - 1);
            StatusMessage = $"{participant.DisplayName} left the session.";
        });

    private void OnSessionEnded(string reason)
        => NotifyOnUi(() =>
        {
            IsConnected = false;
            ParticipantCount = 0;
            _playback?.Clear();
            StatusMessage = reason;
        });

    private void OnConnectionClosed(Exception? exception)
        => NotifyOnUi(() =>
        {
            IsConnected = false;
            ParticipantCount = 0;
            _playback?.Clear();
            StatusMessage = exception is null
                ? "Connection to the signalling server was closed."
                : $"Connection to the signalling server was lost: {exception.Message}";
        });

    private void NotifyOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
