using VScreenPilot.Perception;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class OcrUpscaleMappingTests
{
    [Fact]
    public void ScaleLinesMapsBackToSourceCoordinates()
    {
        var lines = new List<ScreenTextLine>
        {
            new("报表", 90, 60, 30, 12),
        };

        List<ScreenTextLine> scaled = ScreenTextService.ScaleLines(lines, 3);

        Assert.Equal(30, scaled[0].Left);
        Assert.Equal(20, scaled[0].Top);
        Assert.Equal(10, scaled[0].Width);
        Assert.Equal(4, scaled[0].Height);
    }
}
