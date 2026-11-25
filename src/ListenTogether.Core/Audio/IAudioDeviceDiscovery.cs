namespace ListenTogether.Core.Audio;

public interface IAudioDeviceDiscovery
{
    Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default);
}
