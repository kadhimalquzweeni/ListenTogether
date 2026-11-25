using ListenTogether.Core.Audio;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Starts a selected group of capture sources and produces fixed-size mixed PCM blocks
/// on a shared clock. Sources can be added and removed while the session is running.
/// </summary>
public sealed class AudioCaptureMixerSession : IAsyncDisposable
{
    private readonly AudioSourceSelection _selection;
    private readonly IAudioCaptureFactory _captureFactory;
    private readonly TimeSpan _blockInterval;
    private readonly int _framesPerBlock;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly List<IAudioCapture> _captures = [];
    private readonly Dictionary<IAudioCapture, Action<AudioFrame>> _frameHandlers = [];
    private CancellationTokenSource? _clockCancellation;
    private Task? _clockTask;
    private PcmAudioMixer? _mixer;
    private int _isRunning;
    private int _isDisposed;

    public AudioCaptureMixerSession(
        AudioSourceSelection selection,
        IAudioCaptureFactory captureFactory,
        TimeSpan? blockInterval = null)
    {
        _selection = selection ?? throw new ArgumentNullException(nameof(selection));
        _captureFactory = captureFactory ?? throw new ArgumentNullException(nameof(captureFactory));
        _blockInterval = blockInterval ?? TimeSpan.FromMilliseconds(10);

        if (_blockInterval <= TimeSpan.Zero || _blockInterval > TimeSpan.FromMilliseconds(100))
            throw new ArgumentOutOfRangeException(nameof(blockInterval));

        _framesPerBlock = checked((int)Math.Round(AudioFormat.Broadcast.SampleRate * _blockInterval.TotalSeconds));
        if (_framesPerBlock < 1)
            throw new ArgumentOutOfRangeException(nameof(blockInterval));
    }

    public bool IsRunning => Volatile.Read(ref _isRunning) == 1;

    public event Action<AudioFrame>? MixedFrameAvailable;
    public event Action<Exception>? CaptureFaulted;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            if (IsRunning)
                throw new InvalidOperationException("The audio mixing session is already running.");

            _mixer = new PcmAudioMixer(AudioFormat.Broadcast);

            try
            {
                foreach (var source in _selection.Sources)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await AddSourceCoreAsync(source, cancellationToken).ConfigureAwait(false);
                }

                _clockCancellation = new CancellationTokenSource();
                Volatile.Write(ref _isRunning, 1);
                _clockTask = RunClockAsync(_clockCancellation.Token);
            }
            catch
            {
                await StopCapturesAsync().ConfigureAwait(false);
                _mixer = null;
                throw;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Adds and starts a new capture source without restarting existing sources.</summary>
    public async Task AddSourceAsync(AudioSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning || _mixer is null)
                throw new InvalidOperationException("The audio mixing session is not running.");

            await AddSourceCoreAsync(source, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>Stops and removes a source while leaving the other capture sources running.</summary>
    public async Task<bool> RemoveSourceAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("A source ID is required.", nameof(sourceId));

        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning || _mixer is null)
                return false;

            var capture = _captures.FirstOrDefault(item =>
                string.Equals(item.Source.Id, sourceId, StringComparison.Ordinal));
            if (capture is null)
                return false;

            if (_frameHandlers.Remove(capture, out var frameHandler))
                capture.AudioFrameAvailable -= frameHandler;
            capture.CaptureFaulted -= OnCaptureFaulted;

            _captures.Remove(capture);
            await capture.StopAsync(cancellationToken).ConfigureAwait(false);
            await capture.DisposeAsync().ConfigureAwait(false);
            _mixer.RemoveSource(sourceId);
            return true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public void SetGain(string sourceId, float gain)
    {
        if (!IsRunning || _mixer is null)
            throw new InvalidOperationException("The audio mixing session is not running.");

        _mixer.SetGain(sourceId, gain);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsRunning && _captures.Count == 0)
                return;

            Volatile.Write(ref _isRunning, 0);
            if (_clockCancellation is not null)
            {
                await _clockCancellation.CancelAsync().ConfigureAwait(false);
                if (_clockTask is not null)
                {
                    try { await _clockTask.ConfigureAwait(false); }
                    catch (OperationCanceledException) { }
                }

                _clockCancellation.Dispose();
                _clockCancellation = null;
                _clockTask = null;
            }

            await StopCapturesAsync().ConfigureAwait(false);
            _mixer = null;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        await StopAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task AddSourceCoreAsync(AudioSource source, CancellationToken cancellationToken)
    {
        if (_mixer is null)
            throw new InvalidOperationException("The audio mixer has not been initialized.");
        if (_captures.Any(item => string.Equals(item.Source.Id, source.Id, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Audio source '{source.Id}' is already active.");

        IAudioCapture? capture = null;
        var mixerSourceAdded = false;
        try
        {
            var createdCapture = await _captureFactory.CreateAsync(source, cancellationToken).ConfigureAwait(false);
            capture = createdCapture;
            _mixer.AddSource(source.Id);
            mixerSourceAdded = true;

            Action<AudioFrame> frameHandler = frame => OnAudioFrameAvailable(createdCapture, frame);
            _frameHandlers.Add(createdCapture, frameHandler);
            createdCapture.AudioFrameAvailable += frameHandler;
            createdCapture.CaptureFaulted += OnCaptureFaulted;
            _captures.Add(createdCapture);
            cancellationToken.ThrowIfCancellationRequested();
            createdCapture.Start();
        }
        catch
        {
            if (capture is not null)
            {
                if (_frameHandlers.Remove(capture, out var frameHandler))
                    capture.AudioFrameAvailable -= frameHandler;
                capture.CaptureFaulted -= OnCaptureFaulted;
                _captures.Remove(capture);
                try { await capture.StopAsync().ConfigureAwait(false); } catch { }
                try { await capture.DisposeAsync().ConfigureAwait(false); } catch { }
            }

            if (mixerSourceAdded)
                _mixer.RemoveSource(source.Id);
            throw;
        }
    }

    private async Task RunClockAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_blockInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                var mixer = _mixer;
                if (mixer is null)
                    break;

                NotifyMixedFrame(mixer.Mix(_framesPerBlock));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal stop path.
        }
        catch (Exception exception)
        {
            NotifyFault(exception);
        }
    }

    private void OnAudioFrameAvailable(IAudioCapture capture, AudioFrame frame)
    {
        if (!IsRunning)
            return;

        try
        {
            _mixer?.PushFrame(capture.Source.Id, frame);
        }
        catch (Exception exception)
        {
            NotifyFault(exception);
        }
    }

    private void OnCaptureFaulted(Exception exception) => NotifyFault(exception);

    private void NotifyMixedFrame(AudioFrame frame)
    {
        var handlers = MixedFrameAvailable;
        if (handlers is null)
            return;

        foreach (Action<AudioFrame> handler in handlers.GetInvocationList())
        {
            try { handler(frame); }
            catch (Exception exception) { NotifyFault(exception); }
        }
    }

    private void NotifyFault(Exception exception)
    {
        var handlers = CaptureFaulted;
        if (handlers is null)
            return;

        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try { handler(exception); }
            catch { /* Never let observers crash a capture or mixer thread. */ }
        }
    }

    private async Task StopCapturesAsync()
    {
        foreach (var capture in _captures)
        {
            if (_frameHandlers.Remove(capture, out var frameHandler))
                capture.AudioFrameAvailable -= frameHandler;

            capture.CaptureFaulted -= OnCaptureFaulted;
            try { await capture.StopAsync().ConfigureAwait(false); }
            catch (Exception exception) { NotifyFault(exception); }
            try { await capture.DisposeAsync().ConfigureAwait(false); }
            catch (Exception exception) { NotifyFault(exception); }
        }

        _captures.Clear();
    }
}
