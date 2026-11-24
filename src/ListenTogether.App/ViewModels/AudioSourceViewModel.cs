using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using ListenTogether.Core.Audio;
using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.App.ViewModels;

public sealed class AudioSourceViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly IAudioSourceDiscovery _discovery;
    private readonly IAudioCaptureFactory _captureFactory;
    private AudioCaptureMixerSession? _captureSession;
    private AudioSourceItemViewModel? _systemAudioSource;
    private bool _isSystemAudioMode = true;
    private bool _isLoading;
    private bool _isCapturing;
    private string _statusMessage = "Choose system audio or select one or more applications.";
    private double _outputLevel;

    public AudioSourceViewModel(IAudioSourceDiscovery discovery, IAudioCaptureFactory captureFactory)
    {
        _discovery = discovery ?? throw new ArgumentNullException(nameof(discovery));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));

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
                RefreshCommands();
        }
    }

    public bool IsCapturing
    {
        get => _isCapturing;
        private set
        {
            if (SetProperty(ref _isCapturing, value))
                RefreshCommands();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public double OutputLevel
    {
        get => _outputLevel;
        private set => SetProperty(ref _outputLevel, Math.Clamp(value, 0, 100));
    }

    public int SelectedApplicationCount => ApplicationSources.Count(source => source.IsSelected);
    public int AvailableApplicationCount => ApplicationSources.Count;
    public bool CanStart => !IsLoading && !IsCapturing;

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

        AudioSourceSelection selection;
        IReadOnlyList<AudioSourceItemViewModel> selectedItems;

        if (IsSystemAudioMode)
        {
            if (SystemAudioSource is null)
            {
                StatusMessage = "The system audio source wasn't found. Refresh the list and try again.";
                return;
            }

            selectedItems = [SystemAudioSource];
            selection = AudioSourceSelection.System(SystemAudioSource.Source);
        }
        else
        {
            selectedItems = ApplicationSources.Where(source => source.IsSelected).ToArray();
            if (selectedItems.Count == 0)
            {
                StatusMessage = "Select at least one application to share.";
                return;
            }

            selection = AudioSourceSelection.Applications(selectedItems.Select(item => item.Source));
        }

        IsLoading = true;
        StatusMessage = "Starting audio capture…";

        try
        {
            _captureSession = new AudioCaptureMixerSession(selection, _captureFactory);
            foreach (var item in selectedItems)
            {
                item.GainChanged += OnSelectedSourceGainChanged;
            }

            _captureSession.MixedFrameAvailable += OnMixedFrameAvailable;
            _captureSession.CaptureFaulted += OnCaptureFaulted;
            await _captureSession.StartAsync();

            foreach (var item in selectedItems)
                _captureSession.SetGain(item.Source.Id, item.EffectiveGain);

            IsCapturing = true;
            StatusMessage = IsSystemAudioMode
                ? "Capturing all system audio."
                : $"Capturing {selectedItems.Count} selected application(s).";
        }
        catch (Exception exception)
        {
            if (_captureSession is not null)
            {
                _captureSession.MixedFrameAvailable -= OnMixedFrameAvailable;
                _captureSession.CaptureFaulted -= OnCaptureFaulted;
                await _captureSession.DisposeAsync();
                _captureSession = null;
            }

            foreach (var item in selectedItems)
                item.GainChanged -= OnSelectedSourceGainChanged;

            StatusMessage = $"Couldn't start audio capture: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task StopAsync()
    {
        if (_captureSession is null)
            return;

        IsLoading = true;
        StatusMessage = "Stopping audio capture…";

        try
        {
            _captureSession.MixedFrameAvailable -= OnMixedFrameAvailable;
            _captureSession.CaptureFaulted -= OnCaptureFaulted;
            await _captureSession.DisposeAsync();
            _captureSession = null;

            foreach (var item in GetCurrentSelectedItems())
                item.GainChanged -= OnSelectedSourceGainChanged;

            IsCapturing = false;
            OutputLevel = 0;
            StatusMessage = "Audio capture stopped.";
        }
        catch (Exception exception)
        {
            StatusMessage = $"Couldn't stop audio capture cleanly: {exception.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_captureSession is not null)
            await StopAsync();
    }

    private IReadOnlyList<AudioSourceItemViewModel> GetCurrentSelectedItems()
    {
        if (IsSystemAudioMode)
            return SystemAudioSource is null ? [] : [SystemAudioSource];

        return ApplicationSources.Where(source => source.IsSelected).ToArray();
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

    private void OnMixedFrameAvailable(AudioFrame frame)
    {
        var samples = frame.Samples.Span;
        var peak = 0f;
        foreach (var sample in samples)
            peak = Math.Max(peak, Math.Abs(sample));

        var level = peak * 100d;
        Application.Current?.Dispatcher.BeginInvoke(() => OutputLevel = level);
    }

    private void OnCaptureFaulted(Exception exception)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
            StatusMessage = $"Audio capture warning: {exception.Message}");
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
