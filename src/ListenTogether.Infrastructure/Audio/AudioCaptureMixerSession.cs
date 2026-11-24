using ListenTogether.Core.Audio;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Starts a selected group of capture sources and produces fixed-size mixed PCM blocks
/// on a shared clock. The mixed-frame event is the hand-off point for an encoder/transport.
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

                    var capture = await _captureFactory.CreateAsync(source, cancellationToken).ConfigureAwait(false);
                    _captures.Add(capture);
                    _mixer.AddSource(source.Id);

                    Action<AudioFrame> frameHandler = frame => OnAudioFrameAvailable(capture, frame);
                    _frameHandlers.Add(capture, frameHandler);
                    capture.AudioFrameAvailable += frameHandler;
                    capture.CaptureFaulted += OnCaptureFaulted;
                }

                foreach (var capture in _captures)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    capture.Start();
                }

                _clockCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
