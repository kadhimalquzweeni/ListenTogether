using System.Runtime.InteropServices;
using ListenTogether.Core.Audio;
using NAudio.Wave;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Plays decoded remote PCM through the default Windows output device.
/// </summary>
public sealed class WasapiAudioPlayback : IAsyncDisposable
{
    private readonly BoundedPcmWaveProvider _buffer;
    private readonly WasapiPlayer _player;
    private int _isStarted;
    private int _isDisposed;

    public WasapiAudioPlayback(int bufferMilliseconds = 250)
    {
        if (bufferMilliseconds is < 40 or > 2_000)
            throw new ArgumentOutOfRangeException(nameof(bufferMilliseconds));

        var waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(
            AudioFormat.Broadcast.SampleRate,
            AudioFormat.Broadcast.Channels);

        _buffer = new BoundedPcmWaveProvider(
            waveFormat,
            TimeSpan.FromMilliseconds(bufferMilliseconds));

        _player = new WasapiPlayerBuilder()
            .WithLatency(Math.Min(bufferMilliseconds, 100))
            .WithMmcssThreadPriority("Pro Audio")
            .Build();
        _player.Init(_buffer);
    }

    public bool IsPlaying => Volatile.Read(ref _isStarted) == 1;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
        if (Interlocked.Exchange(ref _isStarted, 1) == 0)
            _player.Play();
    }

    public void Write(AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) == 1, this);
        ArgumentNullException.ThrowIfNull(frame);

        if (frame.Format != AudioFormat.Broadcast)
            throw new ArgumentException("Playback requires 48 kHz stereo PCM.", nameof(frame));

        _buffer.AddSamples(MemoryMarshal.AsBytes(frame.Samples.Span));
    }

    public void Clear() => _buffer.Clear();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _isDisposed, 1) == 1)
            return;

        Volatile.Write(ref _isStarted, 0);
        _player.Stop();
        await _player.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// A bounded, thread-safe PCM provider. When the network producer falls behind,
/// the oldest samples are discarded to keep playback latency bounded.
/// </summary>
public sealed class BoundedPcmWaveProvider : IWaveProvider
{
    private readonly object _sync = new();
    private readonly byte[] _buffer;
    private int _readPosition;
    private int _writePosition;
    private int _count;

    public BoundedPcmWaveProvider(WaveFormat waveFormat, TimeSpan bufferDuration)
    {
        WaveFormat = waveFormat ?? throw new ArgumentNullException(nameof(waveFormat));
        if (bufferDuration <= TimeSpan.Zero || bufferDuration > TimeSpan.FromSeconds(10))
            throw new ArgumentOutOfRangeException(nameof(bufferDuration));

        var capacity = checked((int)(waveFormat.AverageBytesPerSecond * bufferDuration.TotalSeconds));
        capacity -= capacity % waveFormat.BlockAlign;
        if (capacity < waveFormat.BlockAlign)
            throw new ArgumentOutOfRangeException(nameof(bufferDuration));

        _buffer = new byte[capacity];
    }

    public WaveFormat WaveFormat { get; }
    public int CapacityBytes => _buffer.Length;

    public int BufferedBytes
    {
        get { lock (_sync) return _count; }
    }

    public void AddSamples(ReadOnlySpan<byte> samples)
    {
        if (samples.Length % WaveFormat.BlockAlign != 0)
            throw new ArgumentException("PCM data must contain complete audio frames.", nameof(samples));

        if (samples.IsEmpty)
            return;

        lock (_sync)
        {
            if (samples.Length > _buffer.Length)
                samples = samples[^_buffer.Length..];

            var overflow = Math.Max(0, _count + samples.Length - _buffer.Length);
            overflow += (WaveFormat.BlockAlign - overflow % WaveFormat.BlockAlign) % WaveFormat.BlockAlign;
            if (overflow > 0)
            {
                _readPosition = (_readPosition + overflow) % _buffer.Length;
                _count -= overflow;
            }

            CopyIntoRing(samples);
            _count += samples.Length;
        }
    }

    public int Read(Span<byte> destination)
    {
        lock (_sync)
        {
            var bytesToRead = Math.Min(destination.Length, _count);
            CopyFromRing(destination[..bytesToRead]);
            destination[bytesToRead..].Clear();
            _count -= bytesToRead;
            return destination.Length;
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _readPosition = 0;
            _writePosition = 0;
            _count = 0;
        }
    }

    private void CopyIntoRing(ReadOnlySpan<byte> source)
    {
        var firstLength = Math.Min(source.Length, _buffer.Length - _writePosition);
        source[..firstLength].CopyTo(_buffer.AsSpan(_writePosition, firstLength));
        source[firstLength..].CopyTo(_buffer);
        _writePosition = (_writePosition + source.Length) % _buffer.Length;
    }

    private void CopyFromRing(Span<byte> destination)
    {
        var firstLength = Math.Min(destination.Length, _buffer.Length - _readPosition);
        _buffer.AsSpan(_readPosition, firstLength).CopyTo(destination[..firstLength]);
        _buffer.AsSpan(0, destination.Length - firstLength).CopyTo(destination[firstLength..]);
        _readPosition = (_readPosition + destination.Length) % _buffer.Length;
    }
}
