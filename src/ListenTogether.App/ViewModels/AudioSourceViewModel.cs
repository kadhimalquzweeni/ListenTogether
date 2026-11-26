using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using ListenTogether.Core.Audio;
using ListenTogether.Core.Sessions;
using ListenTogether.Infrastructure.Audio;
using ListenTogether.Infrastructure.Networking;

namespace ListenTogether.App.ViewModels;

public sealed class AudioSourceViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private const int PacketQueueCapacity = 12;
    private readonly IAudioSourceDiscovery _discovery;
    private readonly IAudioDeviceDiscovery _deviceDiscovery;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly Uri _serverUri;
    private readonly string _displayName;
    private readonly SignalRSessionClientFactory _sessionClientFactory;
    private readonly List<AudioSourceItemViewModel> _activeGainSources = [];
    private readonly SemaphoreSlim _sessionLifecycle = new(1, 1);
    private readonly SemaphoreSlim _sourceSwitchLock = new(1, 1);
    private AdaptiveBitrateController _bitrateController = new();
    private AudioCaptureMixerSession? _captureSession;
    private SignalRSessionClient? _sessionClient;
    private PeerKeyNegotiationCoordinator? _peerNegotiation;
    private UdpPeerCandidateCoordinator? _peerCandidates;
    private UdpPeerTransportCoordinator? _udpTransport;
    private OpusAudioEncoder? _encoder;
    private Channel<EncodedAudioPacket>? _packetChannel;
    private CancellationTokenSource? _streamCancellation;
    private Task? _streamTask;
    private AudioSourceItemViewModel? _systemAudioSource;
    private AudioDevice? _selectedOutputDevice;
    private bool _isSystemAudioMode = true;
    private bool _isLoading;
    private bool _isCapturing;
    private string _statusMessage = "Choose system audio or select one or more applications.";
    private string _sessionCode = string.Empty;
    private double _outputLevel;
    private double _outputRmsLevel;
    private double _shareVolume = 100;
    private int _currentBitrate = AdaptiveBitrateController.DefaultBitrate;
    private int _participantCount;
    private long _packetSequence;
    private long _droppedPacketCount;
    private long _lastMeterUpdateTimestamp;
    private int _requestedBitrate = AdaptiveBitrateController.DefaultBitrate;
    private int _isDisposed;

    public AudioSourceViewModel(
        IAudioSourceDiscovery discovery,
        IAudioCaptureFactory captureFactory,
        Uri serverUri,
        string displayName,
        SignalRSessionClientFactory? sessionClientFactory = null,
        IAudioDeviceDiscovery? deviceDiscovery = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
        _displayName = string.IsNullOrWhiteSpace(displayName) ? throw new ArgumentException("A display name is required.", nameof(displayName)) : displayName.Trim();
        _sessionClientFactory = sessionClientFactory ?? new SignalRSessionClientFactory();
        _deviceDiscovery = deviceDiscovery ?? new WindowsAudioDeviceDiscovery();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && !IsCapturing);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsLoading && !IsCapturing);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsCapturing);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<AudioSourceItemViewModel> ApplicationSources { get; } = [];
    public ObservableCollection<AudioDevice> OutputDevices { get; } = [];
    public ObservableCollection<ParticipantItemViewModel> Participants { get; } = [];

    public AudioSourceItemViewModel? SystemAudioSource
    {
        get => _systemAudioSource;
        private set => SetProperty(ref _systemAudioSource, value);
    }

    public AudioDevice? SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            if (!SetProperty(ref _selectedOutputDevice, value) || SystemAudioSource is null)
                return;

            var previous = SystemAudioSource;
            previous.GainChanged -= OnSelectedSourceGainChanged;
            var replacement = new AudioSourceItemViewModel(
                new AudioSource(previous.Source.Id, previous.Source.DisplayName, AudioSourceKind.System,
                    deviceId: value?.Id),
                previous.IsSelected)
            {
                Volume = previous.Volume,
                IsMuted = previous.IsMuted
            };
            replacement.GainChanged += OnSelectedSourceGainChanged;
            SystemAudioSource = replacement;
            if (IsCapturing)
                _ = ApplySelectedSourcesAsync();
        }
    }

    public bool IsSystemAudioMode
    {
        get => _isSystemAudioMode;
        set
        {
            if (SetProperty(ref _isSystemAudioMode, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsApplicationMode)));
                if (IsCapturing)
                    _ = ApplySelectedSourcesAsync();
            }
        }
    }

    public bool IsApplicationMode => !IsSystemAudioMode;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditSources)));
                RefreshCommands();
            }
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanStart)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanEditSources)));
                RefreshCommands();
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string SessionCode
    {
        get => _sessionCode;
        private set
        {
            if (SetProperty(ref _sessionCode, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSessionCode)));
        }
    }

    public int ParticipantCount
    {
        get => _participantCount;
        private set => SetProperty(ref _participantCount, value);
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

    /// <summary>Master gain applied to the shared mix before Opus encoding.</summary>
    public double ShareVolume
    {
        get => _shareVolume;
        set => SetProperty(ref _shareVolume, Math.Clamp(value, 0, 100));
    }

    public int CurrentBitrate
    {
        get => _currentBitrate;
        private set => SetProperty(ref _currentBitrate, value);
    }

    public long DroppedPacketCount => Interlocked.Read(ref _droppedPacketCount);
    public int SelectedApplicationCount => ApplicationSources.Count(source => source.IsSelected);
    public int AvailableApplicationCount => ApplicationSources.Count;
    public bool CanStart => !IsLoading && !IsCapturing;
    public bool CanEditSources => !IsLoading;
    public bool HasSessionCode => !string.IsNullOrWhiteSpace(SessionCode);

    public ICommand RefreshCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    public async Task RefreshAsync()
    {
        if (IsCapturing)
            return;

        IsLoading = true;
        StatusMessage = "Looking for audio devices and applications…";

        try
        {
            var devices = await _deviceDiscovery.GetOutputDevicesAsync();
            OutputDevices.Clear();
            foreach (var device in devices)
                OutputDevices.Add(device);
            SelectedOutputDevice = devices.FirstOrDefault(device => device.IsDefault) ?? devices.FirstOrDefault();

            var sources = await _discovery.GetAvailableSourcesAsync();
            var system = sources.FirstOrDefault(source => source.Kind == AudioSourceKind.System);
            if (system is not null)
            {
                var systemSource = new AudioSource(system.Id, system.DisplayName, AudioSourceKind.System,
                    deviceId: SelectedOutputDevice?.Id);
                SystemAudioSource = new AudioSourceItemViewModel(systemSource, true);
                SystemAudioSource.GainChanged += OnSelectedSourceGainChanged;
            }

            ApplicationSources.Clear();
            foreach (var source in sources.Where(source => source.Kind == AudioSourceKind.Application))
            {
                var item = new AudioSourceItemViewModel(source);
                item.PropertyChanged += OnApplicationSourcePropertyChanged;
                ApplicationSources.Add(item);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableApplicationCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedApplicationCount)));
            StatusMessage = $"Found {OutputDevices.Count} output devices and {ApplicationSources.Count} applications.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Couldn't refresh audio devices and sources: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task StartAsync()
    {
        if (Volatile.Read(ref _isDisposed) == 1)
            return;
        try { await _sessionLifecycle.WaitAsync(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1 || IsCapturing || IsLoading)
                return;

            var selectedItems = GetSelection(out var selection);
            if (selection is null)
                return;

            IsLoading = true;
            StatusMessage = "Connecting to the signalling server…";

            try
            {
                _sessionClient = _sessionClientFactory.Create(_serverUri);
                _sessionClient.ConnectionClosed += OnConnectionClosed;
                var peerNegotiation = _peerNegotiation = new PeerKeyNegotiationCoordinator(_sessionClient);
                peerNegotiation.PeerSecured += OnPeerSecured;
                var candidateCoordinator = _peerCandidates = new UdpPeerCandidateCoordinator(_sessionClient);
                _sessionClient.ParticipantJoined += OnParticipantJoined;
                _sessionClient.ParticipantLeft += OnParticipantLeft;
                _sessionClient.SessionEnded += OnSessionEnded;

                var invitation = await _sessionClient.CreateSessionAsync(_displayName);
                SessionCode = invitation.Code;
                Participants.Clear();
                Participants.Add(new ParticipantItemViewModel(
                    new SessionParticipant(_displayName, true, _sessionClient.ConnectionId)));
                ParticipantCount = Participants.Count;
                await peerNegotiation.BeginPendingPeersAsync();
                StatusMessage = "Gathering local and STUN UDP candidates…";
                await candidateCoordinator.StartAsync(_serverUri, stunTimeout: TimeSpan.FromSeconds(2));
                _udpTransport = new UdpPeerTransportCoordinator(candidateCoordinator, peerNegotiation, _sessionClient.SessionId);
                _udpTransport.PeerConnected += OnDirectPeerConnected;
                _udpTransport.PeerConnectionAttemptStarted += OnPeerConnectionAttemptStarted;
                _udpTransport.PeerConnectionFailed += OnPeerConnectionFailed;
                await _udpTransport.StartAsync();
                ParticipantCount = Participants.Count;

                _bitrateController = new AdaptiveBitrateController();
                Volatile.Write(ref _requestedBitrate, _bitrateController.CurrentBitrate);
                Interlocked.Exchange(ref _droppedPacketCount, 0);
                _encoder = new OpusAudioEncoder(_bitrateController.CurrentBitrate);
                _packetChannel = Channel.CreateBounded<EncodedAudioPacket>(new BoundedChannelOptions(PacketQueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true
                });
                _streamCancellation = new CancellationTokenSource();
                _streamTask = StreamPacketsAsync(_packetChannel.Reader, _streamCancellation.Token);

                _captureSession = new AudioCaptureMixerSession(selection, _captureFactory);
                _captureSession.MixedFrameAvailable += OnMixedFrameAvailable;
                _captureSession.CaptureFaulted += OnCaptureFaulted;

                foreach (var item in selectedItems)
                {
                    item.GainChanged += OnSelectedSourceGainChanged;
                    if (!_activeGainSources.Contains(item))
                        _activeGainSources.Add(item);
                }

                await _captureSession.StartAsync();
                if (_sessionClient is not { IsConnected: true } || _sessionClient.SessionCode is null)
                    throw new InvalidOperationException("The signalling connection was lost while audio capture was starting.");

                foreach (var item in selectedItems)
                    _captureSession.SetGain(item.Source.Id, item.EffectiveGain);

                IsCapturing = true;
                StatusMessage = $"Sharing audio. Invite code: {SessionCode}. Audio is sent only to authenticated direct UDP peers; there is no server relay fallback.";
            }
            catch (Exception exception)
            {
                var message = exception.Message;
                try { await CleanupSessionAsync(); }
                catch (Exception cleanupException) { message += " Cleanup also failed: " + cleanupException.Message; }

                IsCapturing = false;
                OutputLevel = 0;
                OutputRmsLevel = 0;
                SessionCode = string.Empty;
                ParticipantCount = 0;
                Participants.Clear();
                StatusMessage = "Couldn't start the session: " + message;
            }
            finally { IsLoading = false; }
        }
        finally { _sessionLifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _isDisposed) == 1)
            return;

        try { await _sessionLifecycle.WaitAsync(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (Volatile.Read(ref _isDisposed) == 1 || (_captureSession is null && _sessionClient is null))
                return;

            IsLoading = true;
            StatusMessage = "Stopping the listening session…";
            try
            {
                IsCapturing = false;
                await CleanupSessionAsync();
                OutputLevel = 0;
                OutputRmsLevel = 0;
                SessionCode = string.Empty;
                ParticipantCount = 0;
                Participants.Clear();
                StatusMessage = "Session ended. Your audio is no longer being shared.";
            }
            catch (Exception exception)
            {
                IsCapturing = false;
                StatusMessage = $"Couldn't stop the session cleanly: {exception.Message}";
            }
            finally { IsLoading = false; }
        }
        finally { _sessionLifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        await _sessionLifecycle.WaitAsync();
        try
        {
            IsCapturing = false;
            try { await CleanupSessionAsync(); }
            catch { /* The window is closing; all teardown stages are attempted before this point. */ }
            IsCapturing = false;
        }
        finally
        {
            _sessionLifecycle.Release();
            _sessionLifecycle.Dispose();
            _sourceSwitchLock.Dispose();
        }
    }

    private IReadOnlyList<AudioSourceItemViewModel> GetSelection(out AudioSourceSelection? selection)
    {
        selection = null;
        if (IsSystemAudioMode)
        {
            if (SystemAudioSource is null)
            {
                StatusMessage = "The system audio source wasn't found. Refresh the list and try again.";
                return [];
            }
            selection = AudioSourceSelection.System(SystemAudioSource.Source);
            return [SystemAudioSource];
        }

        var selected = ApplicationSources.Where(source => source.IsSelected).ToArray();
        if (selected.Length == 0)
        {
            StatusMessage = "Select at least one application to share.";
            return [];
        }

        selection = AudioSourceSelection.Applications(selected.Select(item => item.Source));
        return selected;
    }

    private async Task ApplySelectedSourcesAsync()
    {
        var captureSession = _captureSession;
        if (!IsCapturing || captureSession is null || Volatile.Read(ref _isDisposed) == 1)
            return;

        try { await _sourceSwitchLock.WaitAsync(); }
        catch (ObjectDisposedException) { return; }
        try
        {
            if (!IsCapturing || !ReferenceEquals(captureSession, _captureSession))
                return;

            AudioSourceItemViewModel[] desired;
            if (IsSystemAudioMode)
                desired = SystemAudioSource is null ? [] : [SystemAudioSource];
            else
                desired = ApplicationSources.Where(source => source.IsSelected).ToArray();

            foreach (var active in _activeGainSources.ToArray())
            {
                var replacement = desired.FirstOrDefault(item =>
                    string.Equals(item.Source.Id, active.Source.Id, StringComparison.Ordinal));
                if (replacement is not null && Equals(replacement.Source, active.Source))
                    continue;

                await captureSession.RemoveSourceAsync(active.Source.Id);
                active.GainChanged -= OnSelectedSourceGainChanged;
                _activeGainSources.Remove(active);
            }

            foreach (var item in desired)
            {
                if (_activeGainSources.Any(active => Equals(active.Source, item.Source)))
                    continue;

                await captureSession.AddSourceAsync(item.Source);
                captureSession.SetGain(item.Source.Id, item.EffectiveGain);
                item.GainChanged += OnSelectedSourceGainChanged;
                _activeGainSources.Add(item);
            }

            StatusMessage = desired.Length == 0
                ? "No audio sources selected. Choose at least one application to resume sharing audio."
                : $"Updated shared audio sources ({desired.Length} active).";
        }
        catch (Exception exception)
        {
            NotifyOnUi(() => StatusMessage = $"Couldn't switch audio sources: {exception.Message}");
        }
        finally { _sourceSwitchLock.Release(); }
    }

    private async Task StreamPacketsAsync(ChannelReader<EncodedAudioPacket> reader, CancellationToken cancellationToken)
    {
        var packetsSinceMeasurement = 0;
        var lastSendDuration = TimeSpan.Zero;
        try
        {
            await foreach (var packet in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var transport = _udpTransport
                    ?? throw new InvalidOperationException("The direct UDP transport is unavailable. Audio was not sent.");
                var stopwatch = Stopwatch.StartNew();
                var connectedPeers = await transport.SendAudioToConnectedPeersAsync(
                    packet.Payload, packet.Sequence, cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                if (connectedPeers.Count == 0)
                {
                    packetsSinceMeasurement = 0;
                    continue;
                }
                lastSendDuration = stopwatch.Elapsed;

                if (++packetsSinceMeasurement < 10)
                    continue;

                packetsSinceMeasurement = 0;
                var queueDepth = reader.CanCount ? reader.Count : 0;
                var bitrate = _bitrateController.Evaluate(
                    queueDepth,
                    PacketQueueCapacity,
                    Interlocked.Read(ref _droppedPacketCount),
                    lastSendDuration);
                Volatile.Write(ref _requestedBitrate, bitrate);
                NotifyOnUi(() => CurrentBitrate = bitrate);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop path.
        }
        catch (Exception exception)
        {
            NotifyOnUi(() => StatusMessage = $"Streaming connection problem: {exception.Message}");
        }
    }

    private void OnMixedFrameAvailable(AudioFrame frame)
    {
        try
        {
            var volume = (float)(ShareVolume / 100d);
            var samples = frame.Samples.Span;
            AudioFrame encodeFrame = frame;
            if (volume < 0.9999f)
            {
                var scaled = new float[samples.Length];
                for (var index = 0; index < samples.Length; index++)
                    scaled[index] = samples[index] * volume;
                encodeFrame = new AudioFrame(scaled, frame.Format, frame.CapturedAtUtc);
                samples = encodeFrame.Samples.Span;
            }

            var level = AudioLevelMeter.Measure(samples);
            var now = Stopwatch.GetTimestamp();
            var previous = Interlocked.Read(ref _lastMeterUpdateTimestamp);
            if (Stopwatch.GetElapsedTime(previous, now) >= TimeSpan.FromMilliseconds(33) &&
                Interlocked.CompareExchange(ref _lastMeterUpdateTimestamp, now, previous) == previous)
            {
                NotifyOnUi(() =>
                {
                    OutputLevel = level.PeakPercent;
                    OutputRmsLevel = level.RmsPercent;
                });
            }

            var encoder = _encoder;
            var channel = _packetChannel;
            if (encoder is null || channel is null)
                return;

            var requestedBitrate = Volatile.Read(ref _requestedBitrate);
            if (encoder.Bitrate != requestedBitrate)
                encoder.SetBitrate(requestedBitrate);

            foreach (var packet in encoder.Encode(encodeFrame))
            {
                var sequence = Interlocked.Increment(ref _packetSequence);
                if (!channel.Writer.TryWrite(new EncodedAudioPacket(packet, sequence)))
                {
                    Interlocked.Increment(ref _droppedPacketCount);
                    NotifyOnUi(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DroppedPacketCount))));
                }
            }
        }
        catch (Exception exception) { OnCaptureFaulted(exception); }
    }

    private void OnSelectedSourceGainChanged(object? sender, EventArgs e)
    {
        if (sender is not AudioSourceItemViewModel item || _captureSession is null || !IsCapturing)
            return;
        try { _captureSession.SetGain(item.Source.Id, item.EffectiveGain); }
        catch (KeyNotFoundException)
        {
            // A source is being swapped; the next source reconciliation applies its gain.
        }
        catch (Exception exception) { NotifyOnUi(() => StatusMessage = $"Couldn't update volume: {exception.Message}"); }
    }

    private void OnApplicationSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioSourceItemViewModel.IsSelected))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedApplicationCount)));
            if (IsCapturing && IsApplicationMode)
                _ = ApplySelectedSourcesAsync();
        }
    }

    private void OnCaptureFaulted(Exception exception)
        => NotifyOnUi(() => StatusMessage = $"Audio capture warning: {exception.Message}");

    private void OnConnectionClosed(Exception? exception)
        => NotifyOnUi(() =>
        {
            StatusMessage = exception is null
                ? "Disconnected from the signalling server."
                : $"Disconnected from the signalling server: {exception.Message}";
            _ = StopAsync();
        });

    private void OnPeerSecured(string peerConnectionId, PeerKeyAgreementResult result)
        => NotifyOnUi(() => StatusMessage = $"A secure peer link has been established with {peerConnectionId}; direct UDP connectivity is being checked.");

    private void OnDirectPeerConnected(string peerConnectionId, System.Net.IPEndPoint endpoint)
        => NotifyOnUi(() =>
        {
            Participants.FirstOrDefault(item => string.Equals(item.ConnectionId, peerConnectionId, StringComparison.Ordinal))
                ?.SetDirectConnection(true);
            StatusMessage = $"Secure direct UDP audio connection established with peer {peerConnectionId}. Audio is flowing directly.";
        });

    private void OnPeerConnectionAttemptStarted(string peerConnectionId)
        => NotifyOnUi(() => StatusMessage = $"Negotiating direct UDP with listener {peerConnectionId}…");

    private void OnPeerConnectionFailed(string peerConnectionId, string reason)
        => NotifyOnUi(() =>
        {
            Participants.FirstOrDefault(item => string.Equals(item.ConnectionId, peerConnectionId, StringComparison.Ordinal))
                ?.SetConnectionFailure("Direct UDP connection failed");
            StatusMessage = $"Listener {peerConnectionId} is not reachable over direct UDP. {reason}";
        });

    private void OnParticipantJoined(SessionParticipant participant)
        => NotifyOnUi(() =>
        {
            if (!Participants.Any(item => item.ConnectionId == participant.ConnectionId))
                Participants.Add(new ParticipantItemViewModel(participant));
            ParticipantCount = Participants.Count;
            StatusMessage = $"{participant.DisplayName} joined your session.";
        });

    private void OnParticipantLeft(SessionParticipant participant)
        => NotifyOnUi(() =>
        {
            var item = Participants.FirstOrDefault(candidate => candidate.ConnectionId == participant.ConnectionId);
            if (item is not null && !item.IsHost)
                Participants.Remove(item);
            ParticipantCount = Math.Max(1, Participants.Count);
            StatusMessage = $"{participant.DisplayName} left your session.";
        });

    private void OnSessionEnded(string reason)
        => NotifyOnUi(() =>
        {
            StatusMessage = reason;
            _ = StopAsync();
        });

    private void NotifyOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private async Task CleanupSessionAsync()
    {
        await _sourceSwitchLock.WaitAsync();
        try
        {
        Exception? cleanupError = null;
        var captureSession = _captureSession;
        _captureSession = null;
        if (captureSession is not null)
        {
            captureSession.MixedFrameAvailable -= OnMixedFrameAvailable;
            captureSession.CaptureFaulted -= OnCaptureFaulted;
            try { await captureSession.DisposeAsync(); }
            catch (Exception exception) { cleanupError = exception; }
        }

        foreach (var item in _activeGainSources)
            item.GainChanged -= OnSelectedSourceGainChanged;
        _activeGainSources.Clear();

        var packetChannel = _packetChannel;
        _packetChannel = null;
        packetChannel?.Writer.TryComplete();

        var streamCancellation = _streamCancellation;
        _streamCancellation = null;
        if (streamCancellation is not null)
        {
            await streamCancellation.CancelAsync();
            if (_streamTask is not null)
            {
                try { await _streamTask.ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
            }
            streamCancellation.Dispose();
        }
        _streamTask = null;

        _encoder?.Dispose();
        _encoder = null;

        if (_udpTransport is not null)
        {
            _udpTransport.PeerConnected -= OnDirectPeerConnected;
            _udpTransport.PeerConnectionAttemptStarted -= OnPeerConnectionAttemptStarted;
            _udpTransport.PeerConnectionFailed -= OnPeerConnectionFailed;
            try { await _udpTransport.DisposeAsync(); }
            catch (Exception exception) { cleanupError ??= exception; }
            _udpTransport = null;
        }

        _peerCandidates?.Dispose();
        _peerCandidates = null;
        _peerNegotiation?.Dispose();
        _peerNegotiation = null;

        var sessionClient = _sessionClient;
        _sessionClient = null;
        if (sessionClient is not null)
        {
            sessionClient.ConnectionClosed -= OnConnectionClosed;
            sessionClient.ParticipantJoined -= OnParticipantJoined;
            sessionClient.ParticipantLeft -= OnParticipantLeft;
            sessionClient.SessionEnded -= OnSessionEnded;
            try { await sessionClient.DisposeAsync(); }
            catch (Exception exception) { cleanupError ??= exception; }
        }

        if (cleanupError is not null)
            throw cleanupError;
        }
        finally
        {
            _sourceSwitchLock.Release();
        }
    }

    private void RefreshCommands()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private sealed record EncodedAudioPacket(byte[] Payload, long Sequence);
}

public sealed class AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _isExecuting;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_isExecuting && (canExecute?.Invoke() ?? true);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
            return;
        _isExecuting = true;
        RaiseCanExecuteChanged();
        try { await execute(); }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
