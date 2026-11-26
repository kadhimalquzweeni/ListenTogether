using Concentus;
using Concentus.Structs;
using ListenTogether.Core.Audio;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Decodes individual Opus packets into the shared 48 kHz stereo float PCM format.
/// Each remote participant must have its own decoder instance.
/// </summary>
public sealed class OpusAudioDecoder : IDisposable
{
    private const int MaximumFrameSamplesPerChannel = 5_760;
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(
        AudioFormat.Broadcast.SampleRate,
        AudioFormat.Broadcast.Channels);

    private bool _isDisposed;

    public AudioFrame Decode(ReadOnlySpan<byte> packet, DateTimeOffset? capturedAtUtc = null)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (packet.IsEmpty)
            throw new ArgumentException("An Opus packet cannot be empty.", nameof(packet));

        var pcm = new float[MaximumFrameSamplesPerChannel * AudioFormat.Broadcast.Channels];
        var decodedSamplesPerChannel = _decoder.Decode(
            packet,
            pcm,
            MaximumFrameSamplesPerChannel,
            false);

        if (decodedSamplesPerChannel <= 0)
            throw new InvalidOperationException("The Opus decoder returned an empty audio frame.");

        var sampleCount = decodedSamplesPerChannel * AudioFormat.Broadcast.Channels;
        var samples = pcm.AsSpan(0, sampleCount).ToArray();

        return new AudioFrame(samples, AudioFormat.Broadcast, capturedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _decoder.Dispose();
    }
}
