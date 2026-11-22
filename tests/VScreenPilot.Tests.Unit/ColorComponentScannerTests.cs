using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ColorComponentScannerTests
{
    private static (byte[] Mask, byte[] Visited, int[] Queue) Buffers(int width, int height)
    {
        int total = width * height;
        return (new byte[total], new byte[total], new int[total]);
    }

    [Fact]
    public void ScansSinglePixel()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(3, 3);
        mask[4] = 1;
        var scanner = new ColorComponentScanner(mask, 3, 3, queue);

        VisualComponentsOps.ColorComponent component = scanner.Fill(4, visited);

        Assert.Equal((1, 1, 1, 1, 1),
            (component.Left, component.Top, component.Right, component.Bottom, component.Pixels));
    }

    [Fact]
    public void ScansHorizontalRun()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(4, 1);
        mask[0] = mask[1] = mask[2] = 1;
        var scanner = new ColorComponentScanner(mask, 4, 1, queue);

        VisualComponentsOps.ColorComponent component = scanner.Fill(0, visited);

        Assert.Equal(3, component.Pixels);
        Assert.Equal(0, component.Left);
        Assert.Equal(2, component.Right);
    }

    [Fact]
    public void ScansRectangle()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(3, 3);
        for (int index = 0; index < mask.Length; index++)
        {
            mask[index] = 1;
        }

        var scanner = new ColorComponentScanner(mask, 3, 3, queue);
        VisualComponentsOps.ColorComponent component = scanner.Fill(0, visited);

        Assert.Equal(9, component.Pixels);
        Assert.Equal((0, 0, 2, 2),
            (component.Left, component.Top, component.Right, component.Bottom));
    }

    [Fact]
    public void MarksVisitedPixels()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(2, 1);
        mask[0] = mask[1] = 1;
        var scanner = new ColorComponentScanner(mask, 2, 1, queue);

        scanner.Fill(0, visited);

        Assert.All(visited, value => Assert.Equal(1, value));
    }

    [Fact]
    public void DiagonalPixelsAreNotConnected()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(2, 2);
        mask[0] = 1;
        mask[3] = 1;
        var scanner = new ColorComponentScanner(mask, 2, 2, queue);

        VisualComponentsOps.ColorComponent component = scanner.Fill(0, visited);

        Assert.Equal(1, component.Pixels);
        Assert.Equal(0, visited[3]);
    }

    [Fact]
    public void FillOnMaskedOutStartStillReturnsStartPixel()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(2, 2);
        var scanner = new ColorComponentScanner(mask, 2, 2, queue);

        VisualComponentsOps.ColorComponent component = scanner.Fill(0, visited);

        // 调用方只在 mask[start] != 0 时调用 Fill；这里记录该前置条件的行为。
        Assert.Equal(1, component.Pixels);
        Assert.Equal((0, 0), (component.Left, component.Top));
    }

    [Fact]
    public void SeparateRunsProduceTwoComponents()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(3, 1);
        mask[0] = 1;
        mask[2] = 1;
        var scanner = new ColorComponentScanner(mask, 3, 1, queue);

        VisualComponentsOps.ColorComponent first = scanner.Fill(0, visited);
        VisualComponentsOps.ColorComponent second = scanner.Fill(2, visited);

        Assert.Equal((0, 0), (first.Left, first.Right));
        Assert.Equal((2, 2), (second.Left, second.Right));
        Assert.Equal(1, first.Pixels);
        Assert.Equal(1, second.Pixels);
    }

    [Fact]
    public void SecondFillOnVisitedPixelYieldsSinglePixel()
    {
        (byte[] mask, byte[] visited, int[] queue) = Buffers(2, 1);
        mask[0] = mask[1] = 1;
        var scanner = new ColorComponentScanner(mask, 2, 1, queue);

        scanner.Fill(0, visited);
        VisualComponentsOps.ColorComponent component = scanner.Fill(1, visited);

        Assert.Equal(1, component.Pixels);
    }
}
