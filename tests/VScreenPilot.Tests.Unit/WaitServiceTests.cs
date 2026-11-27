using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Session;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class WaitServiceTests
{
    [Fact]
    public void AlreadyHeldReadLockIsReusedInsteadOfNestedAcquire()
    {
        int enterCalls = 0;
        int exitCalls = 0;
        var display = new DisplayInfo(
            @"\\.\DISPLAY9", "Virtual", "VDD", Attached: true, Primary: false,
            X: 0, Y: 0, Width: 10, Height: 10, Frequency: 60, Dpi: 96);
        var service = new WaitService(
            new WaitDisplayContext(() => display, (x, y, width, height) =>
                (new byte[width * height * 4], width, height)),
            new WaitSessionContext(
                _ =>
                {
                    enterCalls++;
                    throw new InvalidOperationException("must not enter read lock again");
                },
                () =>
                {
                    exitCalls++;
                    throw new InvalidOperationException("must not exit outer read lock");
                },
                () => true,
                () => false,
                (_, _, _) => { }),
            new WaitAppContext(
                _ => null,
                () => new List<TrackedApp>(),
                () => new HashSet<int>(),
                null!),
            new WaitFrameContext(
                (_, _, _, _, _) => null,
                () => new JsonArray(),
                _ => null!));

        using JsonDocument condition = JsonDocument.Parse(
            $$"""{"type":"process","pid":{{Environment.ProcessId}},"state":"running"}""");

        WaitForView view = service.Wait(condition.RootElement, timeoutMs: 1000);

        Assert.True(view.Satisfied);
        Assert.Equal(0, enterCalls);
        Assert.Equal(0, exitCalls);
    }

    [Fact]
    public void PixelDiffUsesDocumentedMinChangedRatio()
    {
        using JsonDocument condition = JsonDocument.Parse(
            """{"type":"pixel_diff","region":{"x":0,"y":0,"width":10,"height":10},"min_changed_ratio":0.25}""");

        Assert.Equal(0.25, WaitService.ResolveChangedThreshold(condition.RootElement));
    }

    [Fact]
    public void PixelDiffStillAcceptsLegacyThreshold()
    {
        using JsonDocument condition = JsonDocument.Parse(
            """{"type":"pixel_diff","region":{"x":0,"y":0,"width":10,"height":10},"threshold":0.5}""");

        Assert.Equal(0.5, WaitService.ResolveChangedThreshold(condition.RootElement));
    }

    [Fact]
    public void PixelDiffRejectsOutOfRangeRatio()
    {
        using JsonDocument condition = JsonDocument.Parse(
            """{"type":"pixel_diff","region":{"x":0,"y":0,"width":10,"height":10},"min_changed_ratio":1.5}""");

        ToolException exception = Assert.Throws<ToolException>(
            () => WaitService.ResolveChangedThreshold(condition.RootElement));

        Assert.Equal("E_INVALID_ARG", exception.Code);
    }
}
