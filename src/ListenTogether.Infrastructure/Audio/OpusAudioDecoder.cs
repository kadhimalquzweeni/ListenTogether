using Concentus.Structs;
using ListenTogether.Core.Audio;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Decodes individual Opus packets into the shared 48 kHz stereo float PCM format.
/// Each remote participant must have its own decoder instance.
/// </summary>
public sealed class OpusAudioDecoder
{
    private const int MaximumFrameSamplesPerChannel = 5_760;
    private readonly IOpusDecoder _decoder = OpusCodecFactory.CreateDecoder(
        AudioFormat.Broadcast.SampleRate,
        AudioFormat.Broadcast.Channels);

    public AudioFrame Decode(ReadOnlySpan<byte> packet, DateTimeOffset? capturedAtUtc = null)
    {
        if (packet.IsEmpty)
            throw new ArgumentException("An Opus packet cannot be empty.", nameof(packet));

        var packetBytes = packet.ToArray();
        var pcm = new short[MaximumFrameSamplesPerChannel * AudioFormat.Broadcast.Channels];
        var decodedSamplesPerChannel = _decoder.Decode(
            packetBytes,
            0,
            packetBytes.Length,
            pcm,
            0,
            MaximumFrameSamplesPerChannel,
            false);

        if (decodedSamplesPerChannel <= 0)
            throw new InvalidOperationException("The Opus decoder returned an empty audio frame.");

        var sampleCount = decodedSamplesPerChannel * AudioFormat.Broadcast.Channels;
        var samples = new float[sampleCount];
        for (var index = 0; index < sampleCount; index++)
            samples[index] = pcm[index] / 32768f;

        return new AudioFrame(samples, AudioFormat.Broadcast, capturedAtUtc ?? DateTimeOffset.UtcNow);
    }
}
