using ListenTogether.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Owns a NAudio WASAPI recorder and publishes managed PCM frames.
/// </summary>
internal sealed class WasapiAudioCapture : IAudioCapture
{
    private readonly WasapiRecorder _recorder;
    private int _isCapturing;
    private int _isDisposed;

    public WasapiAudioCapture(AudioSource source, AudioFormat format, WasapiRecorder recorder)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Format = format;
        _recorder = recorder ?? throw new ArgumentNullException(nameof(recorder));
        _recorder.DataAvailable += OnDataAvailable;
    }

    public AudioSource Source { get; }
    public AudioFormat Format { get; }
    public bool IsCapturing => Volatile.Read(ref _isCapturing) == 1;

    public event Action<AudioFrame>? AudioFrameAvailable;
    public event Action<Exception>? CaptureFaulted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);

        if (Interlocked.CompareExchange(ref _isCapturing, 1, 0) != 0)
            throw new InvalidOperationException("Audio capture is already running.");

        try
        {
            _recorder.StartRecording();
        }
        catch
        {
            Volatile.Write(ref _isCapturing, 0);
            throw;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.Exchange(ref _isCapturing, 0) == 1)
            await Task.Run(_recorder.StopRecording).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        _recorder.DataAvailable -= OnDataAvailable;

        try
        {
            if (Interlocked.Exchange(ref _isCapturing, 0) == 1)
                await Task.Run(_recorder.StopRecording).ConfigureAwait(false);
        }
        finally
        {
            await _recorder.DisposeAsync().ConfigureAwait(false);
        }

        GC.SuppressFinalize(this);
    }

    private void OnDataAvailable(
        ReadOnlySpan<byte> buffer,
        AudioClientBufferFlags flags,
        long devicePosition,
        long qpcPosition)
    {
        if (Volatile.Read(ref _isDisposed) == 1 || buffer.IsEmpty)
            return;

        try
        {
            var samples = PcmSampleConverter.FromIeeeFloat32LittleEndian(buffer);
            var frame = new AudioFrame(samples, Format, DateTimeOffset.UtcNow);

            var handlers = AudioFrameAvailable;
            if (handlers is null)
                return;

            foreach (Action<AudioFrame> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(frame);
                }
                catch (Exception exception)
                {
                    NotifyFault(exception);
                }
            }
        }
        catch (Exception exception)
        {
            NotifyFault(exception);
        }
    }

    private void NotifyFault(Exception exception)
    {
        var handlers = CaptureFaulted;
        if (handlers is null)
            return;

        foreach (Action<Exception> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(exception);
            }
            catch
            {
                // Subscriber failures must not escape onto the WASAPI capture thread.
            }
        }
    }
}
