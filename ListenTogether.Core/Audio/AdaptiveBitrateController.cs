namespace ListenTogether.Core.Audio;

/// <summary>
/// Conservative bitrate adaptation based on local send-queue pressure and send duration.
/// This is a local congestion signal, not a measurement of remote packet loss.
/// </summary>
public sealed class AdaptiveBitrateController
{
    public const int MinimumBitrate = 32_000;
    public const int MaximumBitrate = 192_000;
    public const int DefaultBitrate = 128_000;

    private readonly int _minimumBitrate;
    private readonly int _maximumBitrate;
    private int _currentBitrate;
    private int _congestedSamples;
    private int _healthySamples;
    private long _lastDroppedPackets;

    public AdaptiveBitrateController(
        int initialBitrate = DefaultBitrate,
        int minimumBitrate = MinimumBitrate,
        int maximumBitrate = MaximumBitrate)
    {
        if (minimumBitrate < 16_000 || maximumBitrate > 512_000 || minimumBitrate > maximumBitrate)
            throw new ArgumentOutOfRangeException(nameof(minimumBitrate), "The bitrate range must be within 16–512 kbps.");

        if (initialBitrate < minimumBitrate || initialBitrate > maximumBitrate)
            throw new ArgumentOutOfRangeException(nameof(initialBitrate));

        _minimumBitrate = minimumBitrate;
        _maximumBitrate = maximumBitrate;
        _currentBitrate = initialBitrate;
    }

    public int CurrentBitrate => Volatile.Read(ref _currentBitrate);

    public int Evaluate(int queuedPackets, int queueCapacity, long droppedPackets, TimeSpan lastSendDuration)
    {
        if (queueCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (queuedPackets < 0 || queuedPackets > queueCapacity)
            throw new ArgumentOutOfRangeException(nameof(queuedPackets));
        if (droppedPackets < _lastDroppedPackets)
            throw new ArgumentOutOfRangeException(nameof(droppedPackets), "Dropped packet totals cannot decrease.");
        if (lastSendDuration < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lastSendDuration));

        var hasNewDrops = droppedPackets > _lastDroppedPackets;
        _lastDroppedPackets = droppedPackets;

        var congested = hasNewDrops ||
                        queuedPackets >= Math.Ceiling(queueCapacity * 0.75d) ||
                        lastSendDuration >= TimeSpan.FromMilliseconds(30);
        var healthy = !hasNewDrops &&
                      queuedPackets <= Math.Floor(queueCapacity * 0.25d) &&
                      lastSendDuration <= TimeSpan.FromMilliseconds(15);

        if (congested)
        {
            _congestedSamples++;
            _healthySamples = 0;
            if (_congestedSamples >= 2)
            {
                _currentBitrate = Math.Max(_minimumBitrate, (int)(_currentBitrate * 0.8d / 1_000) * 1_000);
                _congestedSamples = 0;
            }
        }
        else if (healthy)
        {
            _healthySamples++;
            _congestedSamples = 0;
            if (_healthySamples >= 5)
            {
                _currentBitrate = Math.Min(_maximumBitrate, _currentBitrate + 8_000);
                _healthySamples = 0;
            }
        }
        else
        {
            _congestedSamples = 0;
            _healthySamples = 0;
        }

        return _currentBitrate;
    }
}
