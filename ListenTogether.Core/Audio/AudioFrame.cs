namespace ListenTogether.Core.Audio;

/// <summary>
/// A copied, interleaved IEEE float PCM buffer. The samples remain valid after the
/// native WASAPI callback returns.
/// </summary>
public sealed record AudioFrame
{
    public AudioFrame(ReadOnlyMemory<float> samples, AudioFormat format, DateTimeOffset capturedAtUtc)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0)
            throw new ArgumentException("A valid audio format is required.", nameof(format));

        if (samples.Length % format.Channels != 0)
            throw new ArgumentException("The sample count must contain complete interleaved frames.", nameof(samples));

        Samples = samples;
        Format = format;
        CapturedAtUtc = capturedAtUtc;
    }

    public ReadOnlyMemory<float> Samples { get; }
    public AudioFormat Format { get; }
    public DateTimeOffset CapturedAtUtc { get; }
    public int FrameCount => Samples.Length / Format.Channels;
}
