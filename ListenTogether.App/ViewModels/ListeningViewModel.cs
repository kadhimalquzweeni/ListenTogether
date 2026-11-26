using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using ListenTogether.Core.Audio;
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
    private readonly SemaphoreSlim _playbackSwitchLock = new(1, 1);
    private readonly IAudioDeviceDiscovery _deviceDiscovery;
    private readonly string _sessionCode;
    private readonly Uri _serverUri;
    private bool _isConnecting;
    private bool _isConnected;
    private string _statusMessage = "Connecting to the listening session…";
    private string _hostDisplayName = "—";
    private int _participantCount;
    private string? _selectedOutputDeviceId;
    private double _volume = 100;
    private bool _isMuted;
    private double _outputLevel;
    private double _outputRmsLevel;
    private string _connectionStatus = "Not connected";

    public ListeningViewModel(Uri serverUri, string displayName, string sessionCode, IAudioDeviceDiscovery? deviceDiscovery = null)
    {
        _sessionCode = sessionCode;
        _deviceDiscovery = deviceDiscovery ?? new WindowsAudioDeviceDiscovery();
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
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];
    public ObservableCollection<ParticipantItemViewModel> Participants { get; } = [];

    public string? SelectedOutputDeviceId
    {
        get => _selectedOutputDeviceId;
        set
        {
            if (SetProperty(ref _selectedOutputDeviceId, value) && IsConnected)
                _ = SwitchOutputDeviceAsync(value);
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (SetProperty(ref _volume, Math.Clamp(value, 0, 100)) && _playback is not null)
                _playback.Volume = (float)(_volume / 100d);
        }
    }

    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetProperty(ref _isMuted, value) && _playback is not null)
                _playback.IsMuted = value;
        }
    }

    public double OutputLevel
    {
        get => _outputLevel;
        private set => SetProperty(ref _outputLevel, Math.Clamp(value, 0, 100));
    }

    public double OutputRmsLevel
    {
        get => _outputRmsLevel;
        private set => SetProperty(ref _outputRmsLevel, Math.Clamp(value, 0, 100));
    }

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

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
                await RefreshOutputDevicesAsync();
                _playback = new WasapiAudioPlayback(outputDeviceId: SelectedOutputDeviceId);
                _playback.Volume = (float)(Volume / 100d);
                _playback.IsMuted = IsMuted;
                var result = await _client.JoinSessionAsync(_sessionCode, _displayName);
                if (Volatile.Read(ref _isDisposed) == 1)
                    return;
                if (_client.SessionCode is null)
                {
                    StatusMessage = "The host ended the session before you could join.";
                    return;
                }

                StartJitterBuffer();
                StatusMessage = "Gathering local and STUN UDP candidates…";

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
                    await StopJitterBufferAsync();
                    _peerNegotiation.Dispose();
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
                Participants.Clear();
                foreach (var participant in result.Participants)
                    Participants.Add(new ParticipantItemViewModel(participant));
                if (!Participants.Any(item => item.ConnectionId == result.JoinedParticipant.ConnectionId))
                    Participants.Add(new ParticipantItemViewModel(result.JoinedParticipant));
                ParticipantCount = Participants.Count;
                ConnectionStatus = "Signalling connected; negotiating secure direct UDP";
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
        if (!IsConnected && !_client.IsConnected && _udpTransport is null && _jitterCancellation is null && _playback is null)
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
        OutputLevel = 0;
        OutputRmsLevel = 0;
        Participants.Clear();
        ParticipantCount = 0;
        ConnectionStatus = "Disconnected";
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
                _udpTransport.PeerConnectionAttemptStarted -= OnPeerConnectionAttemptStarted;
                _udpTransport.PeerConnectionFailed -= OnPeerConnectionFailed;
                _udpTransport.AudioPacketReceived -= OnDirectAudioPacketReceived;
                await _udpTransport.DisposeAsync();
                _udpTransport = null;
            }
            _peerNegotiation.Dispose();
            _peerCandidates.Dispose();
            await _client.DisposeAsync();
            _playbackSwitchLock.Dispose();
        }
        finally
        {
            _lifecycle.Release();
            _lifecycle.Dispose();
        }
    }

    private void OnPeerSecured(string peerConnectionId, PeerKeyAgreementResult result)
        => NotifyOnUi(() =>
        {
            ConnectionStatus = "Secure peer keys confirmed; checking UDP path";
            StatusMessage = "A secure peer link has been established; direct UDP connectivity is being checked.";
        });

    private void OnDirectPeerConnected(string peerConnectionId, System.Net.IPEndPoint endpoint)
        => NotifyOnUi(() =>
        {
            Participants.FirstOrDefault(item => item.ConnectionId == peerConnectionId)?.SetDirectConnection(true);
            ConnectionStatus = "Secure direct UDP connected";
            StatusMessage = $"Secure direct UDP audio connection established with peer {peerConnectionId}.";
        });

    private void OnPeerConnectionAttemptStarted(string peerConnectionId)
        => NotifyOnUi(() => StatusMessage = $"Negotiating direct UDP with peer {peerConnectionId}…");

    private void OnPeerConnectionFailed(string peerConnectionId, string reason)
        => NotifyOnUi(() =>
        {
            Participants.FirstOrDefault(item => item.ConnectionId == peerConnectionId)?.SetConnectionFailure("Direct UDP unavailable");
            ConnectionStatus = "Direct UDP connection unavailable";
            StatusMessage = $"Direct UDP connection to peer {peerConnectionId} is not established. {reason}";
        });


    private async Task RefreshOutputDevicesAsync()
    {
        try
        {
            var devices = await _deviceDiscovery.GetOutputDevicesAsync();
            OutputDevices.Clear();
            foreach (var device in devices)
                OutputDevices.Add(device);
            SelectedOutputDeviceId ??= devices.FirstOrDefault(device => device.IsDefault)?.Id
                ?? devices.FirstOrDefault()?.Id;
        }
        catch (Exception exception)
        {
            ConnectionStatus = "Using the Windows default playback device";
            StatusMessage = $"Couldn't enumerate playback devices: {exception.Message}";
        }
    }

    private async Task SwitchOutputDeviceAsync(string? deviceId)
    {
        if (!IsConnected || Volatile.Read(ref _isDisposed) == 1)
            return;

        try { await _playbackSwitchLock.WaitAsync(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (!IsConnected || Volatile.Read(ref _isDisposed) == 1)
                return;

            var replacement = new WasapiAudioPlayback(outputDeviceId: deviceId);
            replacement.Volume = (float)(Volume / 100d);
            replacement.IsMuted = IsMuted;
            replacement.Start();

            WasapiAudioPlayback? previous;
            lock (_decodeLock)
            {
                previous = _playback;
                _playback = replacement;
            }

            if (previous is not null)
                await previous.DisposeAsync();

            StatusMessage = "Playback device changed. Your listening session remains connected.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Couldn't switch playback device: {exception.Message}";
        }
        finally { _playbackSwitchLock.Release(); }
    }

    private void OnDirectAudioPacketReceived(string peerConnectionId, byte[] packet, ulong sequence)
    {
        NotifyOnUi(() => ConnectionStatus = "Receiving encrypted audio over direct UDP");
        _jitterBuffer.TryEnqueue(sequence, packet);
    }

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
                var level = AudioLevelMeter.Measure(frame.Samples.Span);
                NotifyOnUi(() =>
                {
                    OutputLevel = level.PeakPercent;
                    OutputRmsLevel = level.RmsPercent;
                });
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
            if (!Participants.Any(item => item.ConnectionId == participant.ConnectionId))
                Participants.Add(new ParticipantItemViewModel(participant));
            ParticipantCount = Participants.Count;
            StatusMessage = $"{participant.DisplayName} joined the session.";
        });

    private void OnParticipantLeft(SessionParticipant participant)
        => NotifyOnUi(() =>
        {
            var item = Participants.FirstOrDefault(candidate => candidate.ConnectionId == participant.ConnectionId);
            if (item is not null)
                Participants.Remove(item);
            ParticipantCount = Participants.Count;
            StatusMessage = $"{participant.DisplayName} left the session.";
        });

    private void OnSessionEnded(string reason)
        => NotifyOnUi(() =>
        {
            IsConnected = false;
            ParticipantCount = 0;
            Participants.Clear();
            ConnectionStatus = "Session ended";
            _playback?.Clear();
            StatusMessage = reason;
            _ = LeaveAsync();
        });

    private void OnConnectionClosed(Exception? exception)
        => NotifyOnUi(() =>
        {
            IsConnected = false;
            ParticipantCount = 0;
            Participants.Clear();
            ConnectionStatus = "Signalling disconnected";
            _playback?.Clear();
            StatusMessage = exception is null
                ? "Connection to the signalling server was closed."
                : $"Connection to the signalling server was lost: {exception.Message}";
            _ = LeaveAsync();
        });

    private void NotifyOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
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
