using ListenTogether.Core.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class PcmAudioMixerTests
{
    private static readonly AudioFormat Format = new(48_000, 2);

    [Fact]
    public void Mix_SumsSamplesFromAllRegisteredSources()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one");
        mixer.AddSource("two");
        mixer.PushFrame("one", Frame(0.2f, 0.4f));
        mixer.PushFrame("two", Frame(0.3f, 0.1f));

        var output = mixer.Mix(2);

        Assert.Equal(new[] { 0.5f, 0.5f, 0.5f, 0.5f }, output.Samples.ToArray());
    }

    [Fact]
    public void Mix_AppliesIndependentSourceGain()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("quiet", 0.25f);
        mixer.AddSource("normal", 1f);
        mixer.PushFrame("quiet", Frame(0.8f));
        mixer.PushFrame("normal", Frame(0.2f));

        var output = mixer.Mix(1);

        Assert.Equal(new[] { 0.4f, 0.4f }, output.Samples.ToArray());
    }

    [Fact]
    public void Mix_ClampsOutputToValidPcmRange()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one", 2f);
        mixer.AddSource("two", 2f);
        mixer.PushFrame("one", new AudioFrame(new float[] { 0.8f, -0.8f }, Format, DateTimeOffset.UtcNow));
        mixer.PushFrame("two", new AudioFrame(new float[] { 0.8f, -0.8f }, Format, DateTimeOffset.UtcNow));

        var output = mixer.Mix(1);

        Assert.Equal(new[] { 1f, -1f }, output.Samples.ToArray());
    }

    [Fact]
    public void Mix_InsertsSilenceWhenSourceHasNoSamples()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one");
        mixer.PushFrame("one", Frame(0.5f));

        var output = mixer.Mix(2);

        Assert.Equal(new[] { 0.5f, 0.5f, 0f, 0f }, output.Samples.ToArray());
    }

    [Fact]
    public void Mix_ConsumesSamplesAcrossConsecutiveBlocks()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one");
        mixer.PushFrame("one", Frame(0.1f, 0.2f, 0.3f));

        var first = mixer.Mix(1);
        var second = mixer.Mix(1);

        Assert.Equal(new[] { 0.1f, 0.1f }, first.Samples.ToArray());
        Assert.Equal(new[] { 0.2f, 0.2f }, second.Samples.ToArray());
    }

    [Fact]
    public void SetGain_UpdatesOnlySelectedSource()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one");
        mixer.AddSource("two");
        mixer.SetGain("one", 0.5f);

        Assert.Equal(0.5f, mixer.GetGain("one"));
        Assert.Equal(1f, mixer.GetGain("two"));
    }

    [Fact]
    public void PushFrame_RejectsUnknownSource()
    {
        var mixer = new PcmAudioMixer(Format);

        Assert.Throws<KeyNotFoundException>(() => mixer.PushFrame("missing", Frame(0.1f)));
    }

    [Fact]
    public void PushFrame_RejectsMismatchedFormat()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one");
        var frame = new AudioFrame(new float[2], new AudioFormat(44_100, 2), DateTimeOffset.UtcNow);

        Assert.Throws<ArgumentException>(() => mixer.PushFrame("one", frame));
    }

    [Fact]
    public void AddSource_RejectsDuplicateIds()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one");

        Assert.Throws<InvalidOperationException>(() => mixer.AddSource("one"));
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(2.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void AddSource_RejectsInvalidGain(float gain)
    {
        var mixer = new PcmAudioMixer(Format);

        Assert.Throws<ArgumentOutOfRangeException>(() => mixer.AddSource("one", gain));
    }

    [Fact]
    public void RemoveSource_RemovesBufferedAudioAndGain()
    {
        var mixer = new PcmAudioMixer(Format);
        mixer.AddSource("one");
        mixer.PushFrame("one", Frame(0.7f));

        Assert.True(mixer.RemoveSource("one"));
        Assert.False(mixer.RemoveSource("one"));
        Assert.Equal(0, mixer.SourceCount);
        Assert.Equal(new[] { 0f, 0f }, mixer.Mix(1).Samples.ToArray());
    }

    private static AudioFrame Frame(params float[] perChannelValues)
    {
        var samples = new float[perChannelValues.Length * Format.Channels];
        for (var frame = 0; frame < perChannelValues.Length; frame++)
        {
            for (var channel = 0; channel < Format.Channels; channel++)
                samples[frame * Format.Channels + channel] = perChannelValues[frame];
        }

        return new AudioFrame(samples, Format, DateTimeOffset.UtcNow);
    }
}
