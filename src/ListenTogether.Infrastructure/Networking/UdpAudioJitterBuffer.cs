namespace ListenTogether.Infrastructure.Networking;

/// <summary>
/// Small sequence-aware reorder buffer for direct Opus frames. It waits briefly
/// for out-of-order datagrams, skips a missing frame after the configured gap
/// delay, and rejects duplicates and frames older than the playback cursor.
/// </summary>
public sealed class UdpAudioJitterBuffer
{
    private readonly object _sync = new();
    private readonly SortedDictionary<ulong, BufferedFrame> _pending = [];
    private readonly TimeSpan _latency;
    private readonly int _capacity;
    private ulong _expectedSequence;
    private bool _hasExpectedSequence;
    private bool _playoutStarted;
    private DateTimeOffset _firstPacketAt;
    private DateTimeOffset? _gapStartedAt;

    public UdpAudioJitterBuffer(TimeSpan? latency = null, int capacity = 32)
    {
        _latency = latency ?? TimeSpan.FromMilliseconds(40);
        if (_latency < TimeSpan.Zero || _latency > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(latency));
        if (capacity is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int BufferedFrameCount
    {
        get { lock (_sync) return _pending.Count; }
    }

    public bool TryEnqueue(
        ulong sequence,
        ReadOnlyMemory<byte> payload,
        DateTimeOffset? receivedAt = null)
    {
        if (sequence == 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (payload.IsEmpty)
            throw new ArgumentException("An Opus frame cannot be empty.", nameof(payload));

        var timestamp = receivedAt ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (_playoutStarted && sequence < _expectedSequence)
                return false;
            if (_pending.ContainsKey(sequence) || _pending.Count >= _capacity)
                return false;

            if (!_hasExpectedSequence)
            {
                _expectedSequence = sequence;
                _firstPacketAt = timestamp;
                _hasExpectedSequence = true;
            }
            else if (!_playoutStarted && sequence < _expectedSequence)
            {
                _expectedSequence = sequence;
            }

            _pending.Add(sequence, new BufferedFrame(payload.ToArray(), timestamp));
            return true;
        }
    }

    public IReadOnlyList<UdpAudioFrame> DrainReady(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        lock (_sync)
        {
            if (!_hasExpectedSequence || _pending.Count == 0)
                return Array.Empty<UdpAudioFrame>();

            if (!_playoutStarted)
            {
                if (timestamp - _firstPacketAt < _latency)
                    return Array.Empty<UdpAudioFrame>();
                _playoutStarted = true;
            }

            var ready = new List<UdpAudioFrame>();
            while (_pending.Count > 0)
            {
                if (_pending.Remove(_expectedSequence, out var expected))
                {
                    ready.Add(new UdpAudioFrame(_expectedSequence, expected.Payload));
                    if (_expectedSequence == ulong.MaxValue)
                    {
                        _hasExpectedSequence = false;
                        break;
                    }

                    _expectedSequence++;
                    _gapStartedAt = null;
                    continue;
                }

                var next = _pending.First();
                if (next.Key < _expectedSequence)
                {
                    _pending.Remove(next.Key);
                    continue;
                }

                _gapStartedAt ??= timestamp;
                if (timestamp - _gapStartedAt < _latency)
                    break;

                _expectedSequence = next.Key;
                _gapStartedAt = null;
            }

            return ready;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _pending.Clear();
            _expectedSequence = 0;
            _hasExpectedSequence = false;
            _playoutStarted = false;
            _firstPacketAt = default;
            _gapStartedAt = null;
        }
    }

    private sealed record BufferedFrame(byte[] Payload, DateTimeOffset ReceivedAt);
}

public sealed record UdpAudioFrame(ulong Sequence, byte[] Payload);
