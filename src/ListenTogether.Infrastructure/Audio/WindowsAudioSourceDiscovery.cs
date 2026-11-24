using System.ComponentModel;
using System.Diagnostics;
using ListenTogether.Core.Audio;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Finds user-facing Windows processes that can be selected for process-loopback capture.
/// The system-wide source is always included separately.
/// </summary>
public sealed class WindowsAudioSourceDiscovery : IAudioSourceDiscovery
{
    public Task<IReadOnlyList<AudioSource>> GetAvailableSourcesAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<AudioSource>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sources = new List<AudioSource>
            {
                new("system:default-output", "All system audio", AudioSourceKind.System)
            };

            var processes = Process.GetProcesses();
            try
            {
                foreach (var process in processes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        if (process.Id == Environment.ProcessId ||
                            process.HasExited ||
                            process.MainWindowHandle == IntPtr.Zero)
                        {
                            continue;
                        }

                        var processId = process.Id;
                        var processName = process.ProcessName;
                        if (string.IsNullOrWhiteSpace(processName))
                            continue;

                        sources.Add(new AudioSource(
                            $"process:{processId}",
                            processName,
                            AudioSourceKind.Application,
                            processId));
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or
                        Win32Exception or
                        NotSupportedException)
                    {
                        // A process can exit or become inaccessible while the list is being refreshed.
                    }
                }
            }
            finally
            {
                foreach (var process in processes)
                    process.Dispose();
            }

            return Array.AsReadOnly(sources
                .OrderBy(source => source.Kind == AudioSourceKind.System ? 0 : 1)
                .ThenBy(source => source.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToArray());
        }, cancellationToken);
    }
}
