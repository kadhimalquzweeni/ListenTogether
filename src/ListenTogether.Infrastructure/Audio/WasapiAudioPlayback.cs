using ListenTogether.Core.Audio;
using NAudio.Wave;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Plays decoded remote PCM through the default Windows output device.
/// </summary>
public sealed class WasapiAudioPlayback : IAsyncDisposable
{
    private readonly BufferedWaveProvider _buffer;
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

        _buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(bufferMilliseconds),
            DiscardOnBufferOverflow = true,
            ReadFully = true
        };

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

        var samples = frame.Samples.Span;
        var bytes = new byte[samples.Length * sizeof(float)];
        Buffer.BlockCopy(samples.ToArray(), 0, bytes, 0, bytes.Length);
        _buffer.AddSamples(bytes, 0, bytes.Length);
    }

    public void Clear() => _buffer.ClearBuffer();

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
