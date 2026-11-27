using VScreenPilot.Perception;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class BitmapOpsTests
{
    [Fact]
    public void UpscaleDoublesDimensionsAndPreservesOpaqueAlpha()
    {
        byte[] source =
        [
            0, 0, 0, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            255, 255, 255, 255,
        ];

        (byte[] output, int width, int height) = BitmapOps.Upscale(source, 2, 2, 2);

        Assert.Equal(4, width);
        Assert.Equal(4, height);
        Assert.Equal(4 * 4 * 4, output.Length);

        for (int index = 3; index < output.Length; index += 4)
        {
            Assert.Equal(255, output[index]);
        }
    }
}
