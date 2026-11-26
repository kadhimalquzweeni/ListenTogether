using ListenTogether.Infrastructure.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class OpusAudioEncoderBitrateTests
{
    [Fact]
    public void SetBitrate_ChangesTargetWithoutResettingBufferedSamples()
    {
        using var encoder = new OpusAudioEncoder();
        var partialFrame = new ListenTogether.Core.Audio.AudioFrame(
            new float[480 * 2],
            ListenTogether.Core.Audio.AudioFormat.Broadcast,
            DateTimeOffset.UtcNow);

        Assert.Empty(encoder.Encode(partialFrame));
        encoder.SetBitrate(96_000);

        Assert.Equal(96_000, encoder.Bitrate);
        Assert.Equal(480 * 2, encoder.PendingSampleCount);
    }

    [Theory]
    [InlineData(15_999)]
    [InlineData(512_001)]
    public void SetBitrate_RejectsUnsupportedBitrate(int bitrate)
    {
        using var encoder = new OpusAudioEncoder();

        Assert.Throws<ArgumentOutOfRangeException>(() => encoder.SetBitrate(bitrate));
    }
}
