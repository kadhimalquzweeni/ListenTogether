using ListenTogether.Core.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class AdaptiveBitrateControllerTests
{
    [Fact]
    public void Evaluate_ReducesBitrateAfterSustainedCongestion()
    {
        var controller = new AdaptiveBitrateController();

        Assert.Equal(128_000, controller.Evaluate(20, 20, 0, TimeSpan.FromMilliseconds(35)));
        Assert.Equal(102_000, controller.Evaluate(20, 20, 0, TimeSpan.FromMilliseconds(35)));
    }

    [Fact]
    public void Evaluate_IncreasesBitrateGraduallyWhenQueueIsHealthy()
    {
        var controller = new AdaptiveBitrateController();

        for (var i = 0; i < 4; i++)
            Assert.Equal(128_000, controller.Evaluate(1, 20, 0, TimeSpan.FromMilliseconds(5)));

        Assert.Equal(136_000, controller.Evaluate(1, 20, 0, TimeSpan.FromMilliseconds(5)));
    }

    [Fact]
    public void Evaluate_DroppedPacketTriggersBitrateReductionAfterSecondCongestedSample()
    {
        var controller = new AdaptiveBitrateController();

        controller.Evaluate(1, 20, 1, TimeSpan.FromMilliseconds(5));
        Assert.Equal(102_000, controller.Evaluate(20, 20, 1, TimeSpan.FromMilliseconds(35)));
    }

    [Fact]
    public void Evaluate_NeverReducesBelowConfiguredMinimum()
    {
        var controller = new AdaptiveBitrateController(initialBitrate: 40_000, minimumBitrate: 32_000, maximumBitrate: 192_000);

        for (var i = 0; i < 20; i++)
            controller.Evaluate(20, 20, i + 1, TimeSpan.FromMilliseconds(40));

        Assert.Equal(32_000, controller.CurrentBitrate);
    }

    [Fact]
    public void Constructor_RejectsInitialBitrateOutsideConfiguredRange()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new AdaptiveBitrateController(initialBitrate: 20_000));
    }

    [Fact]
    public void Evaluate_RejectsInvalidQueueMeasurements()
    {
        var controller = new AdaptiveBitrateController();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            controller.Evaluate(3, 0, 0, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            controller.Evaluate(21, 20, 0, TimeSpan.Zero));
    }
}
