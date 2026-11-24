using ListenTogether.Core.Audio;
using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class OpusAudioCodecTests
{
    [Fact]
    public void Encode_DoesNotEmitPacketUntilTwentyMillisecondsAreBuffered()
    {
        var encoder = new OpusAudioEncoder();

        var packets = encoder.Encode(Frame(Enumerable.Repeat(0.1f, 480 * 2).ToArray()));

        Assert.Empty(packets);
        Assert.Equal(480 * 2, encoder.PendingSampleCount);
    }

    [Fact]
    public void Encode_EmitsOnePacketForTwentyMilliseconds()
    {
        var encoder = new OpusAudioEncoder();

        var packets = encoder.Encode(Frame(Enumerable.Repeat(0.1f, 960 * 2).ToArray()));

        var packet = Assert.Single(packets);
        Assert.InRange(packet.Length, 1, OpusAudioEncoder.MaximumPacketBytes);
        Assert.Equal(0, encoder.PendingSampleCount);
    }

    [Fact]
    public void Encode_AccumulatesAcrossInputFramesAndEmitsMultiplePackets()
    {
        var encoder = new OpusAudioEncoder();
        var tenMilliseconds = Frame(Enumerable.Repeat(0.2f, 480 * 2).ToArray());

        Assert.Empty(encoder.Encode(tenMilliseconds));
        var packets = encoder.Encode(tenMilliseconds);

        Assert.Single(packets);
    }

    [Fact]
    public void Encode_RejectsNonBroadcastFormat()
    {
        var encoder = new OpusAudioEncoder();
        var frame = new AudioFrame(new float[882], new AudioFormat(44_100, 2), DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => encoder.Encode(frame));
    }

    [Fact]
    public void Decode_RoundTripsAnEncodedTone()
    {
        var encoder = new OpusAudioEncoder();
        var decoder = new OpusAudioDecoder();
        var samples = new float[960 * 2];

        for (var frame = 0; frame < 960; frame++)
        {
            var value = 0.25f * MathF.Sin(2f * MathF.PI * 440f * frame / 48_000f);
            samples[frame * 2] = value;
            samples[frame * 2 + 1] = value;
        }

        var packet = Assert.Single(encoder.Encode(Frame(samples)));
        var decoded = decoder.Decode(packet);

        Assert.Equal(AudioFormat.Broadcast, decoded.Format);
        Assert.Equal(960, decoded.FrameCount);
        Assert.All(decoded.Samples.ToArray(), sample => Assert.True(float.IsFinite(sample)));
        Assert.Contains(decoded.Samples.ToArray(), sample => Math.Abs(sample) > 0.01f);
    }

    [Fact]
    public void Decode_RejectsEmptyPacket()
    {
        var decoder = new OpusAudioDecoder();

        Assert.Throws<ArgumentException>(() => decoder.Decode([]));
    }

    private static AudioFrame Frame(float[] samples)
        => new(samples, AudioFormat.Broadcast, DateTimeOffset.UtcNow);
}
