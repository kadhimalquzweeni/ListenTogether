namespace ListenTogether.Core.Audio;

/// <summary>
/// A bounded, clock-driven PCM mixer. Captured source frames are queued independently;
/// each Mix call consumes one fixed-size block from every source, inserting silence for
/// sources that have not supplied enough samples yet.
/// </summary>
public sealed class PcmAudioMixer
{
    private readonly object _sync = new();
    private readonly Dictionary<string, SourceBuffer> _sources = new(StringComparer.Ordinal);
    private readonly int _maximumBufferedSamples;

    public PcmAudioMixer(AudioFormat format, int maximumBufferedMilliseconds = 500)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0)
            throw new ArgumentException("A valid audio format is required.", nameof(format));

        if (maximumBufferedMilliseconds is < 20 or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedMilliseconds));

        Format = format;
        _maximumBufferedSamples = checked(
            format.SampleRate * format.Channels * maximumBufferedMilliseconds / 1_000);
    }

    public AudioFormat Format { get; }

    public int SourceCount
    {
        get
        {
            lock (_sync)
                return _sources.Count;
        }
    }

    public void AddSource(string sourceId, float gain = 1f)
    {
        ValidateSourceId(sourceId);
        ValidateGain(gain);

        lock (_sync)
        {
            if (!_sources.TryAdd(sourceId, new SourceBuffer(gain)))
                throw new InvalidOperationException($"Audio source '{sourceId}' is already registered.");
        }
    }

    public bool RemoveSource(string sourceId)
    {
        ValidateSourceId(sourceId);

        lock (_sync)
            return _sources.Remove(sourceId);
    }

    public void SetGain(string sourceId, float gain)
    {
        ValidateGain(gain);

        lock (_sync)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                throw new KeyNotFoundException($"Audio source '{sourceId}' is not registered.");

            source.Gain = gain;
        }
    }

    public float GetGain(string sourceId)
    {
        ValidateSourceId(sourceId);

        lock (_sync)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                throw new KeyNotFoundException($"Audio source '{sourceId}' is not registered.");

            return source.Gain;
        }
    }

    public void PushFrame(string sourceId, AudioFrame frame)
    {
        ValidateSourceId(sourceId);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Format != Format)
            throw new ArgumentException("The audio frame format must match the mixer's format.", nameof(frame));

        lock (_sync)
        {
            if (!_sources.TryGetValue(sourceId, out var source))
                throw new KeyNotFoundException($"Audio source '{sourceId}' is not registered.");

            foreach (var sample in frame.Samples.Span)
                source.Samples.Enqueue(float.IsFinite(sample) ? sample : 0f);

            // Bound latency and memory if a producer runs faster than the mixer clock.
            while (source.Samples.Count > _maximumBufferedSamples)
                source.Samples.Dequeue();
        }
    }

    public AudioFrame Mix(int frameCount, DateTimeOffset? capturedAtUtc = null)
    {
        if (frameCount is < 1 or > 48_000)
            throw new ArgumentOutOfRangeException(nameof(frameCount));

        var sampleCount = checked(frameCount * Format.Channels);
        var mixed = new float[sampleCount];

        lock (_sync)
        {
            foreach (var source in _sources.Values)
            {
                for (var index = 0; index < sampleCount; index++)
                {
                    if (source.Samples.TryDequeue(out var sample))
                        mixed[index] += sample * source.Gain;
                }
            }
        }

        // Hard limiting guarantees valid float PCM even when several applications peak
        // at the same time. A later mastering stage can replace this with a look-ahead limiter.
        for (var index = 0; index < mixed.Length; index++)
            mixed[index] = Math.Clamp(mixed[index], -1f, 1f);

        return new AudioFrame(mixed, Format, capturedAtUtc ?? DateTimeOffset.UtcNow);
    }

    private static void ValidateSourceId(string sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
            throw new ArgumentException("A source ID is required.", nameof(sourceId));
    }

    private static void ValidateGain(float gain)
    {
        if (!float.IsFinite(gain) || gain is < 0f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(gain), "Gain must be between 0 and 2.");
    }

    private sealed class SourceBuffer(float gain)
    {
        public Queue<float> Samples { get; } = new();
        public float Gain { get; set; } = gain;
    }
}
