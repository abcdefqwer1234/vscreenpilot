using System.Buffers.Binary;
using VScreenPilot.Perception;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class PngWriterUpscaleTests
{
    [Fact]
    public void ScaleFourUpscalesDimensions()
    {
        const int width = 2;
        const int height = 2;
        var bgra = new byte[width * height * 4];

        for (int index = 0; index < bgra.Length; index += 4)
        {
            bgra[index] = 0;
            bgra[index + 1] = 0;
            bgra[index + 2] = 255;
            bgra[index + 3] = 255;
        }

        byte[] png = PngWriter.Encode(bgra, width, height, 4.0, out int outputWidth, out int outputHeight);

        Assert.Equal(8, outputWidth);
        Assert.Equal(8, outputHeight);
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(8u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
    }

    [Fact]
    public void ScaleIsClampedAtFour()
    {
        var bgra = new byte[4 * 4 * 4];
        PngWriter.Encode(bgra, 4, 4, 8.0, out int outputWidth, out int outputHeight);

        Assert.Equal(16, outputWidth);
        Assert.Equal(16, outputHeight);
    }
}
