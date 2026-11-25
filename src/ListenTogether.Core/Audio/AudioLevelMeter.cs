namespace ListenTogether.Core.Audio;

/// <summary>Measures peak and RMS levels without retaining or allocating the input samples.</summary>
public static class AudioLevelMeter
{
    public static AudioLevelMeasurement Measure(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return new AudioLevelMeasurement(0, 0, double.NegativeInfinity);

        double sumSquares = 0;
        var peak = 0f;
        foreach (var value in samples)
        {
            var sample = float.IsFinite(value) ? Math.Clamp(Math.Abs(value), 0f, 1f) : 0f;
            peak = Math.Max(peak, sample);
            sumSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / samples.Length);
        var peakDbfs = peak <= 0 ? double.NegativeInfinity : 20d * Math.Log10(peak);
        return new AudioLevelMeasurement(peak * 100d, rms * 100d, peakDbfs);
    }
}

public readonly record struct AudioLevelMeasurement(
    double PeakPercent,
    double RmsPercent,
    double PeakDbfs);
