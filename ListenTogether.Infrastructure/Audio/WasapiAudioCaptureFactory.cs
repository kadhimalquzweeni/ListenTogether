using System.Runtime.Versioning;
using ListenTogether.Core.Audio;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>Creates system-wide or process-specific WASAPI loopback captures.</summary>
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

        MMDeviceEnumerator? enumerator = null;
        MMDevice? device = null;
        WasapiRecorder? recorder = null;
        try
        {
            switch (source.Kind)
            {
                case AudioSourceKind.System:
                    if (!string.IsNullOrWhiteSpace(source.DeviceId))
                    {
                        enumerator = new MMDeviceEnumerator();
                        device = enumerator.GetDevice(source.DeviceId);
                        builder.WithDevice(device);
                    }
                    builder.WithLoopbackCapture().WithPollingSync();
                    break;

                case AudioSourceKind.Application:
                    if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
                        throw new PlatformNotSupportedException(
                            "Per-application audio capture requires Windows 10 version 2004 (build 19041).");

                    var processId = source.ProcessId
                        ?? throw new ArgumentException("Application sources must have a process ID.", nameof(source));
                    builder.WithProcessLoopback(
                        checked((uint)processId),
                        ProcessLoopbackMode.IncludeTargetProcessTree);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(source), "The selected audio source kind is not supported.");
            }

            recorder = await builder.BuildAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var capture = new WasapiAudioCapture(source, format, recorder, device, enumerator);
            recorder = null;
            device = null;
            enumerator = null;
            return capture;
        }
        catch
        {
            if (recorder is not null)
                await recorder.DisposeAsync().ConfigureAwait(false);
            device?.Dispose();
            enumerator?.Dispose();
            throw;
        }
    }
}
