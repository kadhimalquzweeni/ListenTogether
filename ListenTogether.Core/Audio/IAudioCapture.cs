namespace ListenTogether.Core.Audio;

/// <summary>
/// A single live audio source. Implementations copy native buffers before publishing
/// them so consumers can process frames asynchronously.
/// </summary>
public interface IAudioCapture : IAsyncDisposable
{
    AudioSource Source { get; }
    AudioFormat Format { get; }
    bool IsCapturing { get; }

    event Action<AudioFrame>? AudioFrameAvailable;
    event Action<Exception>? CaptureFaulted;

    void Start();
    Task StopAsync(CancellationToken cancellationToken = default);
}
