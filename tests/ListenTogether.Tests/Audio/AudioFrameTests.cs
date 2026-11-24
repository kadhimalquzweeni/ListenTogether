using ListenTogether.Core.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class AudioFrameTests
{
    [Fact]
    public void Constructor_StoresFormatAndFrameCount()
    {
        var format = new AudioFormat(48_000, 2);
        var samples = new float[960];

        var frame = new AudioFrame(samples, format, DateTimeOffset.UnixEpoch);

        Assert.Equal(format, frame.Format);
        Assert.Equal(480, frame.FrameCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, frame.CapturedAtUtc);
    }

    [Fact]
    public void Constructor_RejectsIncompleteInterleavedFrame()
    {
        var format = new AudioFormat(48_000, 2);

        Assert.Throws<ArgumentException>(() =>
            new AudioFrame(new float[3], format, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(48_000, 0)]
    [InlineData(7_999, 2)]
    [InlineData(48_000, 9)]
    public void AudioFormat_RejectsInvalidValues(int sampleRate, int channels)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioFormat(sampleRate, channels));
    }
}
