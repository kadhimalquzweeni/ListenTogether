namespace ListenTogether.Core.Audio;

public interface IAudioCaptureFactory
{
    Task<IAudioCapture> CreateAsync(AudioSource source, CancellationToken cancellationToken = default);
}
