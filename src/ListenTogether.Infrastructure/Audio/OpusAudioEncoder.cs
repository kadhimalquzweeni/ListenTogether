using Concentus;
using Concentus.Enums;
using Concentus.Structs;
using ListenTogether.Core.Audio;

namespace ListenTogether.Infrastructure.Audio;

/// <summary>
/// Converts the mixer's 10 ms float PCM blocks into standard 20 ms Opus packets.
/// Encoder state is intentionally owned by one instance and used from one thread.
/// </summary>
public sealed class OpusAudioEncoder : IDisposable
{
    public const int FrameDurationMilliseconds = 20;
    public const int FrameSamplesPerChannel = 48_000 * FrameDurationMilliseconds / 1_000;
    public const int MaximumPacketBytes = 1_275;

    private readonly IOpusEncoder _encoder;
    private readonly short[] _pending = new short[FrameSamplesPerChannel * AudioFormat.Broadcast.Channels];
    private int _pendingSamples;
    private bool _isDisposed;

    public OpusAudioEncoder(int bitrate = 128_000)
    {
        if (bitrate is < 16_000 or > 512_000)
            throw new ArgumentOutOfRangeException(nameof(bitrate));

        _encoder = OpusCodecFactory.CreateEncoder(
            AudioFormat.Broadcast.SampleRate,
            AudioFormat.Broadcast.Channels,
            OpusApplication.OPUS_APPLICATION_AUDIO);
        _encoder.Bitrate = bitrate;
    }

    public int PendingSampleCount => _pendingSamples;

    public IReadOnlyList<byte[]> Encode(AudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Format != AudioFormat.Broadcast)
            throw new ArgumentException("Opus streaming requires 48 kHz stereo PCM.", nameof(frame));

        var packets = new List<byte[]>();
        foreach (var sample in frame.Samples.Span)
        {
            var scaled = (int)MathF.Round(Math.Clamp(sample, -1f, 1f) * short.MaxValue);
            _pending[_pendingSamples++] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);

            if (_pendingSamples != _pending.Length)
                continue;

            var output = new byte[MaximumPacketBytes];
            var encodedBytes = _encoder.Encode(
                _pending.AsSpan(),
                FrameSamplesPerChannel,
                output.AsSpan(),
                output.Length);

            if (encodedBytes <= 0)
                throw new InvalidOperationException($"The Opus encoder returned an invalid packet length: {encodedBytes}.");

            packets.Add(output.AsSpan(0, encodedBytes).ToArray());
            _pendingSamples = 0;
        }

        return packets;
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        Array.Clear(_pending);
        _pendingSamples = 0;
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;
        _isDisposed = true;
        _encoder.Dispose();
    }
}
