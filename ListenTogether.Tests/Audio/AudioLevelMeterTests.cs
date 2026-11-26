using ListenTogether.Core.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class AudioLevelMeterTests
{
    [Fact]
    public void Measure_ReturnsZeroForEmptySamples()
    {
        var result = AudioLevelMeter.Measure([]);

        Assert.Equal(0, result.PeakPercent);
        Assert.Equal(0, result.RmsPercent);
        Assert.True(double.IsNegativeInfinity(result.PeakDbfs));
    }

    [Fact]
    public void Measure_ReportsPeakAndRmsForKnownSignal()
    {
        var result = AudioLevelMeter.Measure([0.5f, -0.5f, 0.25f, -0.25f]);

        Assert.Equal(50, result.PeakPercent, 3);
        Assert.Equal(Math.Sqrt((0.25 + 0.25 + 0.0625 + 0.0625) / 4) * 100, result.RmsPercent, 3);
        Assert.Equal(20 * Math.Log10(0.5), result.PeakDbfs, 3);
    }

    [Fact]
    public void Measure_ClampsAndIgnoresNonFiniteSamples()
    {
        var result = AudioLevelMeter.Measure([2f, float.NaN, float.PositiveInfinity, -0.5f]);

        Assert.Equal(100, result.PeakPercent);
        Assert.Equal(Math.Sqrt((1 + 0.25) / 4) * 100, result.RmsPercent, 3);
    }
}
