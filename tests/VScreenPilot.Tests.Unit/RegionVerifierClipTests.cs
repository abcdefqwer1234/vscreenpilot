using System.Text.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Session;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class RegionVerifierClipTests
{
    [Fact]
    public void ClippedAssertUsesIntersectionAndReportsRequestedRegion()
    {
        int capturedWidth = 0;
        int capturedHeight = 0;
        var display = new DisplayInfo(
            @"\\.\DISPLAY9", "Virtual", "VDD", Attached: true, Primary: false,
            X: 0, Y: 0, Width: 10, Height: 10, Frequency: 60, Dpi: 96);
        var verifier = new RegionVerifier(new RegionVerifierContext(
            () => display,
            (x, y, width, height) =>
            {
                capturedWidth = width;
                capturedHeight = height;
                return (new byte[width * height * 4], width, height);
            },
            _ => null,
            (_, _, _, _) => null,
            () => null,
            () => Array.Empty<FrameSnapshot>()));

        using JsonDocument region = JsonDocument.Parse(
            """{"x":5,"y":0,"width":10,"height":10}""");
        using JsonDocument expectation = JsonDocument.Parse(
            """{"color":"#000000","coverage":1.0}""");

        AssertRegionView view = verifier.Assert(
            region.RootElement, expectation.RootElement, clipOutOfBounds: true);

        Assert.True(view.Passed);
        Assert.True(view.Clipped);
        Assert.Equal(5, capturedWidth);
        Assert.Equal(10, capturedHeight);
        Assert.Equal(5, view.RequestedRegion!.X);
        Assert.Equal(10, view.RequestedRegion.Width);
    }
}
