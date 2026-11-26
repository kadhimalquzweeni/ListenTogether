using ListenTogether.Infrastructure.Audio;
using NAudio.Wave;

namespace ListenTogether.Tests.Audio;

public sealed class BoundedPcmWaveProviderTests
{
    private static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);

    [Fact]
    public void Read_ReturnsQueuedSamplesAndZeroFillsUnderrun()
    {
        var provider = new BoundedPcmWaveProvider(Format, TimeSpan.FromMilliseconds(20));
        var source = Enumerable.Range(0, 8).Select(value => (byte)value).ToArray();
        var destination = new byte[16];

        provider.AddSamples(source);
        provider.Read(destination);

        Assert.Equal(source, destination[..8]);
        Assert.All(destination[8..], value => Assert.Equal((byte)0, value));
        Assert.Equal(0, provider.BufferedBytes);
    }

    [Fact]
    public void AddSamples_DropsOldestWholeAudioFramesWhenBufferIsFull()
    {
        var provider = new BoundedPcmWaveProvider(Format, TimeSpan.FromMilliseconds(1));
        var capacity = provider.CapacityBytes;
        var initial = Enumerable.Repeat((byte)1, capacity).ToArray();
        var latest = Enumerable.Repeat((byte)2, Format.BlockAlign).ToArray();

        provider.AddSamples(initial);
        provider.AddSamples(latest);

        var destination = new byte[capacity];
        provider.Read(destination);

        Assert.All(destination[..^Format.BlockAlign], value => Assert.Equal((byte)1, value));
        Assert.All(destination[^Format.BlockAlign..], value => Assert.Equal((byte)2, value));
        Assert.Equal(capacity, destination.Length);
    }

    [Fact]
    public void Clear_RemovesAllBufferedSamples()
    {
        var provider = new BoundedPcmWaveProvider(Format, TimeSpan.FromMilliseconds(20));
        provider.AddSamples(new byte[Format.BlockAlign]);

        provider.Clear();

        Assert.Equal(0, provider.BufferedBytes);
    }

    [Fact]
    public void AddSamples_RejectsIncompleteAudioFrame()
    {
        var provider = new BoundedPcmWaveProvider(Format, TimeSpan.FromMilliseconds(20));

        Assert.Throws<ArgumentException>(() => provider.AddSamples(new byte[Format.BlockAlign - 1]));
    }
}
