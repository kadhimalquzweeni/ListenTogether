namespace ListenTogether.Core.Audio;

public interface IAudioSourceDiscovery
{
    Task<IReadOnlyList<AudioSource>> GetAvailableSourcesAsync(CancellationToken cancellationToken = default);
}
