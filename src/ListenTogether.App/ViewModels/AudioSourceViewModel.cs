using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly IAudioSourceDiscovery _discovery;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly Uri _serverUri;
    private readonly string _displayName;
    private readonly SignalRSessionClientFactory _sessionClientFactory;
    private readonly List<AudioSourceItemViewModel> _activeGainSources = [];
    private AudioCaptureMixerSession? _captureSession;
    private SignalRSessionClient? _sessionClient;
    private OpusAudioEncoder? _encoder;
    private Channel<EncodedAudioPacket>? _packetChannel;
    private CancellationTokenSource? _streamCancellation;
    private Task? _streamTask;
    private AudioSourceItemViewModel? _systemAudioSource;
    private bool _isSystemAudioMode = true;
    private bool _isLoading;
    private bool _isCapturing;
    private string _statusMessage = "Choose system audio or select one or more applications.";
    private string _sessionCode = string.Empty;
    private double _outputLevel;
    private int _participantCount;
    private long _packetSequence;

    public AudioSourceViewModel(
        IAudioSourceDiscovery discovery,
        IAudioCaptureFactory captureFactory,
        Uri serverUri,
        string displayName,
        SignalRSessionClientFactory? sessionClientFactory = null)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _serverUri = serverUri ?? throw new ArgumentNullException(nameof(serverUri));
        _displayName = string.IsNullOrWhiteSpace(displayName) ? throw new ArgumentException("A display name is required.", nameof(displayName)) : displayName.Trim();
        _sessionClientFactory = sessionClientFactory ?? new SignalRSessionClientFactory();

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsLoading && !IsCapturing);
        StartCommand = new AsyncRelayCommand(StartAsync, () => !IsLoading && !IsCapturing);
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsCapturing);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AudioSourceItemViewModel> ApplicationSources { get; } = [];

    public AudioSourceItemViewModel? SystemAudioSource
    {
        get => _systemAudioSource;
        private set => SetProperty(ref _systemAudioSource, value);
    }

    public bool IsSystemAudioMode
    {
        get => _isSystemAudioMode;
        set
        {
            if (SetProperty(ref _isSystemAudioMode, value))
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsApplicationMode)));
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
        private set => SetProperty(ref _sessionCode, value);
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

    public int SelectedApplicationCount => ApplicationSources.Count(source => source.IsSelected);
    public int AvailableApplicationCount => ApplicationSources.Count;
    public bool CanStart => !IsLoading && !IsCapturing;
    public bool CanEditSources => !IsLoading && !IsCapturing;

    public ICommand RefreshCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }

    public async Task RefreshAsync()
    {
        if (IsCapturing)
            return;

        IsLoading = true;
        StatusMessage = "Looking for audio applications…";

        try
        {
            var sources = await _discovery.GetAvailableSourcesAsync();
            var system = sources.FirstOrDefault(source => source.Kind == AudioSourceKind.System);
            SystemAudioSource = system is null ? null : new AudioSourceItemViewModel(system, true);

            ApplicationSources.Clear();
            foreach (var source in sources.Where(source => source.Kind == AudioSourceKind.Application))
            {
                var item = new AudioSourceItemViewModel(source);
                item.PropertyChanged += OnApplicationSourcePropertyChanged;
                ApplicationSources.Add(item);
            }

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AvailableApplicationCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedApplicationCount)));
            StatusMessage = $"Found {ApplicationSources.Count} applications. Choose what you want to share.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Couldn't refresh audio sources: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task StartAsync()
    {
        if (IsCapturing || IsLoading)
            return;

        var selectedItems = GetSelection(out var selection);
        if (selection is null)
            return;

        IsLoading = true;
        StatusMessage = "Connecting to the listening relay…";

        try
        {
            _sessionClient = _sessionClientFactory.Create(_serverUri);
            _sessionClient.ConnectionClosed += OnConnectionClosed;
            _sessionClient.ParticipantJoined += OnParticipantJoined;
            _sessionClient.ParticipantLeft += OnParticipantLeft;
            _sessionClient.SessionEnded += OnSessionEnded;
            var invitation = await _sessionClient.CreateSessionAsync(_displayName);
            SessionCode = invitation.Code;
            ParticipantCount = 1;

            _encoder = new OpusAudioEncoder();
            _packetChannel = Channel.CreateBounded<EncodedAudioPacket>(new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
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
                _activeGainSources.Add(item);
            }

            await _captureSession.StartAsync();

            foreach (var item in selectedItems)
                _captureSession.SetGain(item.Source.Id, item.EffectiveGain);

            IsCapturing = true;
            StatusMessage = $"Sharing audio. Invite code: {SessionCode}";
        }
        catch (Exception exception)
        {
            await CleanupSessionAsync();
            StatusMessage = $"Couldn't start the session: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task StopAsync()
    {
        if (_captureSession is null && _sessionClient is null)
            return;

        IsLoading = true;
        StatusMessage = "Stopping the listening session…";

        try
        {
            await CleanupSessionAsync();
            IsCapturing = false;
            OutputLevel = 0;
            SessionCode = string.Empty;
            ParticipantCount = 0;
            StatusMessage = "Session ended. Your audio is no longer being shared.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Couldn't stop the session cleanly: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_captureSession is not null || _sessionClient is not null)
            await StopAsync();
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

    private async Task StreamPacketsAsync(ChannelReader<EncodedAudioPacket> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var packet in reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var client = _sessionClient;
                if (client is null)
                    break;

                await client.SendAudioPacketAsync(packet.Payload, packet.Sequence, cancellationToken).ConfigureAwait(false);
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
            var samples = frame.Samples.Span;
            var peak = 0f;
            foreach (var sample in samples)
                peak = Math.Max(peak, Math.Abs(sample));

            var level = peak * 100d;
            NotifyOnUi(() => OutputLevel = level);

            var encoder = _encoder;
            var channel = _packetChannel;
            if (encoder is null || channel is null)
                return;

            foreach (var packet in encoder.Encode(frame))
            {
                var sequence = Interlocked.Increment(ref _packetSequence);
                channel.Writer.TryWrite(new EncodedAudioPacket(packet, sequence));
            }
        }
        catch (Exception exception)
        {
            OnCaptureFaulted(exception);
        }
    }

    private void OnSelectedSourceGainChanged(object? sender, EventArgs e)
    {
        if (sender is not AudioSourceItemViewModel item || _captureSession is null || !IsCapturing)
            return;

        try { _captureSession.SetGain(item.Source.Id, item.EffectiveGain); }
        catch (Exception exception) { StatusMessage = $"Couldn't update volume: {exception.Message}"; }
    }

    private void OnApplicationSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AudioSourceItemViewModel.IsSelected))
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedApplicationCount)));
    }

    private void OnCaptureFaulted(Exception exception)
        => NotifyOnUi(() => StatusMessage = $"Audio capture warning: {exception.Message}");

    private void OnConnectionClosed(Exception? exception)
        => NotifyOnUi(() => StatusMessage = exception is null
            ? "Disconnected from the relay."
            : $"Disconnected from the relay: {exception.Message}");

    private void OnParticipantJoined(SessionParticipant participant)
        => NotifyOnUi(() =>
        {
            ParticipantCount++;
            StatusMessage = $"{participant.DisplayName} joined your session.";
        });

    private void OnParticipantLeft(SessionParticipant participant)
        => NotifyOnUi(() =>
        {
            ParticipantCount = Math.Max(1, ParticipantCount - 1);
            StatusMessage = $"{participant.DisplayName} left your session.";
        });

    private void OnSessionEnded(string reason)
        => NotifyOnUi(() =>
        {
            IsCapturing = false;
            ParticipantCount = 0;
            StatusMessage = reason;
        });

    private void NotifyOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
            action();
        else
            dispatcher.BeginInvoke(action);
    }

    private async Task CleanupSessionAsync()
    {
        if (_captureSession is not null)
        {
            _captureSession.MixedFrameAvailable -= OnMixedFrameAvailable;
            _captureSession.CaptureFaulted -= OnCaptureFaulted;
            await _captureSession.DisposeAsync();
            _captureSession = null;
        }

        foreach (var item in _activeGainSources)
            item.GainChanged -= OnSelectedSourceGainChanged;
        _activeGainSources.Clear();

        if (_packetChannel is not null)
        {
            _packetChannel.Writer.TryComplete();
            _packetChannel = null;
        }

        if (_streamCancellation is not null)
        {
            await _streamCancellation.CancelAsync();
            if (_streamTask is not null)
            {
                try { await _streamTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }

            _streamCancellation.Dispose();
            _streamCancellation = null;
            _streamTask = null;
        }

        _encoder?.Dispose();
        _encoder = null;

        if (_sessionClient is not null)
        {
            _sessionClient.ConnectionClosed -= OnConnectionClosed;
            _sessionClient.ParticipantJoined -= OnParticipantJoined;
            _sessionClient.ParticipantLeft -= OnParticipantLeft;
            _sessionClient.SessionEnded -= OnSessionEnded;
            await _sessionClient.DisposeAsync();
            _sessionClient = null;
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
