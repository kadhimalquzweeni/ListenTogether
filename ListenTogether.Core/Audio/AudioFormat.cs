namespace ListenTogether.Core.Audio;

/// <summary>
/// The interleaved PCM format used inside the capture and streaming pipeline.
/// </summary>
public readonly record struct AudioFormat
{
    public AudioFormat(int sampleRate, int channels)
    {
        if (sampleRate is < 8_000 or > 192_000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "Sample rate must be between 8 kHz and 192 kHz.");

        if (channels is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(channels), "Channel count must be between one and eight.");

        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public int BytesPerSample => sizeof(float);

    public static AudioFormat Broadcast { get; } = new(48_000, 2);
}
