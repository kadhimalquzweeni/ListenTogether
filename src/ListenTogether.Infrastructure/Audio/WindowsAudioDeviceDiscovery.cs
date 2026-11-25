using ListenTogether.Core.Audio;
using NAudio.CoreAudioApi;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>Discovers active Windows playback endpoints through WASAPI.</summary>
public sealed class WindowsAudioDeviceDiscovery : IAudioDeviceDiscovery
{
    public Task<IReadOnlyList<AudioDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows audio devices are only available on Windows.");

        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        using var defaultEndpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var defaultId = defaultEndpoint.ID;
        var devices = new List<AudioDevice>();
        using var endpoints = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        foreach (var endpoint in endpoints)
        {
            using (endpoint)
            {
                devices.Add(new AudioDevice(
                    endpoint.ID,
                    endpoint.FriendlyName,
                    string.Equals(endpoint.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }

        var ordered = devices.OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        return Task.FromResult<IReadOnlyList<AudioDevice>>(Array.AsReadOnly(ordered));
    }
}
