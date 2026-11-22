using System.Buffers.Binary;
using VScreenPilot.Perception;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class PngOverlayTests
{
    private static (byte[] Bgra, PngCanvas Canvas) CreateCanvas(int width, int height)
    {
        var data = new byte[width * height * 4];
        return (data, new PngCanvas(data, width, height));
    }

    private static (byte B, byte G, byte R, byte A) Pixel(
        byte[] bgra, int width, int x, int y)
    {
        int index = (y * width + x) * 4;
        return (bgra[index], bgra[index + 1], bgra[index + 2], bgra[index + 3]);
    }

    [Fact]
    public void DrawBoxPaintsBorder()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(4, 4);

        PngOverlay.DrawBox(canvas, new PngBox(0, 0, 4, 4), (10, 20, 30));

        Assert.Equal((30, 20, 10, 255), Pixel(data, 4, 0, 0));
    }

    [Fact]
    public void DrawBoxLeavesInteriorUntouched()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(5, 5);

        PngOverlay.DrawBox(canvas, new PngBox(0, 0, 5, 5), (255, 0, 0));

        Assert.Equal((0, 0, 0, 0), Pixel(data, 5, 2, 2));
    }

    [Fact]
    public void DrawBoxIgnoresEmptyBox()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(2, 2);

        PngOverlay.DrawBox(canvas, new PngBox(0, 0, 0, 0), (255, 0, 0));

        Assert.All(data, value => Assert.Equal(0, value));
    }

    [Fact]
    public void DrawBoxClampsNegativeOrigin()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(3, 3);

        PngOverlay.DrawBox(canvas, new PngBox(-2, -2, 4, 4), (0, 255, 0));

        Assert.Equal((0, 255, 0, 255), Pixel(data, 3, 0, 0));
    }

    [Fact]
    public void DrawBoxThicknessWidensBorder()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(5, 5);

        PngOverlay.DrawBox(canvas, new PngBox(0, 0, 5, 5), (255, 255, 255), thickness: 2);

        Assert.Equal(255, Pixel(data, 5, 1, 1).R);
    }

    [Fact]
    public void DrawGridPaintsVerticalLine()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(4, 4);

        PngOverlay.DrawGrid(canvas, step: 2);

        Assert.True(Pixel(data, 4, 2, 0).A > 0);
    }

    [Fact]
    public void DrawGridIgnoresNonPositiveStep()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(2, 2);

        PngOverlay.DrawGrid(canvas, step: 0);

        Assert.All(data, value => Assert.Equal(0, value));
    }

    [Fact]
    public void DrawNumberPaintsWhiteBackground()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(16, 16);

        PngOverlay.DrawNumber(canvas, 0, 0, 1, scale: 1);

        Assert.Equal((245, 245, 245, 255), Pixel(data, 16, 0, 0));
    }

    [Fact]
    public void DrawNumberClampsToCanvas()
    {
        (byte[] data, PngCanvas canvas) = CreateCanvas(4, 4);

        PngOverlay.DrawNumber(canvas, 100, 100, 99, scale: 1);

        Assert.True(Pixel(data, 4, 3, 3).A > 0);
    }

    [Fact]
    public void ColorForCyclesPalette()
    {
        Assert.Equal(PngOverlay.ColorFor(0), PngOverlay.ColorFor(8));
        Assert.NotEqual(PngOverlay.ColorFor(0), PngOverlay.ColorFor(1));
    }
}
