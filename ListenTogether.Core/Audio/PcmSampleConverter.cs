using System.Buffers.Binary;

namespace ListenTogether.Core.Audio;

/// <summary>
/// Converts the IEEE float PCM bytes emitted by WASAPI into managed samples.
/// </summary>
public static class PcmSampleConverter
{
    public static float[] FromIeeeFloat32LittleEndian(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
            throw new ArgumentException("The byte count must be divisible by four for IEEE float PCM.", nameof(bytes));

        var samples = new float[bytes.Length / sizeof(float)];

        for (var index = 0; index < samples.Length; index++)
        {
            var offset = index * sizeof(float);
            var bits = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(float)));
            var sample = BitConverter.Int32BitsToSingle(bits);

            // Invalid floating-point values must never poison the mixer or encoder.
            samples[index] = float.IsFinite(sample) ? sample : 0f;
        }

        return samples;
    }
}
