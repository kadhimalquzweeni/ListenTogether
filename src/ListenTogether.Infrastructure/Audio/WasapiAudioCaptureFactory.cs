using System.Runtime.Versioning;
using ListenTogether.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Creates system-wide or process-specific WASAPI loopback captures.
/// </summary>
public sealed class WasapiAudioCaptureFactory : IAudioCaptureFactory
{
    public async Task<IAudioCapture> CreateAsync(
        AudioSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("WASAPI audio capture is only available on Windows.");

        cancellationToken.ThrowIfCancellationRequested();

        var format = AudioFormat.Broadcast;
        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

        var builder = new WasapiRecorderBuilder()
            .WithFormat(waveFormat)
            .WithBufferLength(20)
            .WithMmcssThreadPriority("Pro Audio");

        switch (source.Kind)
        {
            case AudioSourceKind.System:
                builder.WithLoopbackCapture().WithPollingSync();
                break;

            case AudioSourceKind.Application:
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                {
                    throw new PlatformNotSupportedException(
                        "Per-application audio capture requires Windows 10 version 2004 (build 19041) or later.");
                }

                var processId = source.ProcessId
                    ?? throw new ArgumentException("Application sources must have a process ID.", nameof(source));

                builder.WithProcessLoopback(
                    checked((uint)processId),
                    ProcessLoopbackMode.IncludeTargetProcessTree);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(source), "The selected audio source kind is not supported.");
        }

        var recorder = await builder.BuildAsync().ConfigureAwait(false);

        if (cancellationToken.IsCancellationRequested)
        {
            await recorder.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return new WasapiAudioCapture(source, format, recorder);
    }
}
