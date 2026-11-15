using VScreenPilot.Perception;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class PngWriterTests
{
    [Fact]
    public void RoundTripPreservesPixels()
    {
        const int width = 16;
        const int height = 8;
        byte[] bgra = BuildGradient(width, height);

        byte[] png = PngWriter.Encode(bgra, width, height, 1.0, out int outWidth, out int outHeight);
        PngImage image = PngReader.Decode(png);

        Assert.Equal(width, outWidth);
        Assert.Equal(height, outHeight);
        Assert.Equal(width, image.Width);
        Assert.Equal(height, image.Height);

        // 取 (1,1) 像素：B=17, G=36, R=23 → RGBA(23,36,17,255)
        int index = (1 * width + 1) * 4;
        Assert.Equal(23, image.Rgba[index]);
        Assert.Equal(36, image.Rgba[index + 1]);
        Assert.Equal(17, image.Rgba[index + 2]);
        Assert.Equal(255, image.Rgba[index + 3]);
    }

    [Fact]
    public void ScaleHalvesDimensions()
    {
        byte[] bgra = BuildGradient(32, 16);
        byte[] png = PngWriter.Encode(bgra, 32, 16, 0.5, out int outWidth, out int outHeight);
        PngImage image = PngReader.Decode(png);

        Assert.Equal(16, outWidth);
        Assert.Equal(8, outHeight);
        Assert.Equal(16, image.Width);
        Assert.Equal(8, image.Height);
    }

    [Fact]
    public void FilteredGradientCompressesBelowRawSize()
    {
        byte[] bgra = BuildGradient(128, 64);
        byte[] png = PngWriter.Encode(bgra, 128, 64, 1.0, out _, out _);

        // 渐变 + Up/Sub 过滤后应明显小于未压缩的 128*64*4 = 32768 字节。
        Assert.True(png.Length < bgra.Length / 2, $"png={png.Length} raw={bgra.Length}");
    }

    [Fact]
    public void DecodeRejectsTruncatedData()
    {
        Assert.Throws<PngFormatException>(() => PngReader.Decode(new byte[] { 1, 2, 3 }));
    }

    [Fact]
    public void DecodeRejectsCorruptedCrc()
    {
        byte[] png = PngWriter.Encode(BuildGradient(8, 8), 8, 8, 1.0, out _, out _);

        // 改 IHDR 数据但保留原 CRC，必须在解码前被 CRC 拦下。
        png[20] ^= 0x01;

        PngFormatException error = Assert.Throws<PngFormatException>(() => PngReader.Decode(png));
        Assert.Contains("CRC", error.Message);
    }

    private static byte[] BuildGradient(int width, int height)
    {
        var bgra = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int index = (y * width + x) * 4;
                bgra[index] = (byte)(x * 255 / Math.Max(1, width - 1));
                bgra[index + 1] = (byte)(y * 255 / Math.Max(1, height - 1));
                bgra[index + 2] = (byte)((x + y) * 255 / Math.Max(1, width + height - 2));
                bgra[index + 3] = 255;
            }
        }

        return bgra;
    }
}
