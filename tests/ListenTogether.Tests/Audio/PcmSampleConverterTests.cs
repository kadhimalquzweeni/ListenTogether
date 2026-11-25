using System.Buffers.Binary;
using ListenTogether.Core.Audio;

namespace ListenTogether.Tests.Audio;

public sealed class PcmSampleConverterTests
{
    [Fact]
    public void FromIeeeFloat32LittleEndian_ConvertsSamples()
    {
        var bytes = Encode(0f, 0.25f, -0.5f, 1f);

        var samples = PcmSampleConverter.FromIeeeFloat32LittleEndian(bytes);

        Assert.Equal(new[] { 0f, 0.25f, -0.5f, 1f }, samples);
    }

    [Fact]
    public void FromIeeeFloat32LittleEndian_ReturnsEmptyForEmptyBuffer()
    {
        var samples = PcmSampleConverter.FromIeeeFloat32LittleEndian([]);

        Assert.Empty(samples);
    }

    [Fact]
    public void FromIeeeFloat32LittleEndian_RejectsPartialSample()
    {
        Assert.Throws<ArgumentException>(() =>
            PcmSampleConverter.FromIeeeFloat32LittleEndian([0x00, 0x00, 0x00]));
    }

    [Fact]
    public void FromIeeeFloat32LittleEndian_ReplacesNonFiniteSamplesWithSilence()
    {
        var bytes = Encode(float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f);

        var samples = PcmSampleConverter.FromIeeeFloat32LittleEndian(bytes);

        Assert.Equal(new[] { 0f, 0f, 0f, 0.5f }, samples);
    }

    private static byte[] Encode(params float[] samples)
    {
        var bytes = new byte[samples.Length * sizeof(float)];

        for (var index = 0; index < samples.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                bytes.AsSpan(index * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(samples[index]));
        }

        return bytes;
    }
}
