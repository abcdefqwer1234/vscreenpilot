using VScreenPilot.Platform.Displays;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class DisplayTopologyTests
{
    private static DisplayInfo Parsec(
        string deviceName,
        bool attached = false,
        int width = 0,
        int height = 0) =>
        new(
            deviceName,
            "Parsec Virtual Display Adapter",
            @"ROOT\PSCCDD0\0000",
            attached,
            Primary: false,
            X: 0,
            Y: 0,
            Width: width,
            Height: height,
            Frequency: attached ? 60 : 0,
            Dpi: 0);

    private static DisplayInfo Physical(string deviceName) =>
        new(
            deviceName,
            "Intel(R) Arc(TM) 140T GPU (16GB)",
            @"PCI\VEN_8086",
            Attached: true,
            Primary: true,
            X: 0,
            Y: 0,
            Width: 3072,
            Height: 1920,
            Frequency: 120,
            Dpi: 168);

    [Fact]
    public void DetachedAdaptersWithoutModeAreNotLive()
    {
        Assert.False(DisplayTopology.IsLive(Parsec(@"\\.\DISPLAY5")));
        Assert.False(DisplayTopology.IsLive(Parsec(@"\\.\DISPLAY20")));

        // 驱动预枚举的 detached 适配器即使带默认模式，也算可用（0.1.0 的判定）。
        Assert.True(DisplayTopology.IsLive(Parsec(@"\\.\DISPLAY6", width: 1024, height: 768)));
        Assert.True(DisplayTopology.IsLive(Parsec(@"\\.\DISPLAY7", attached: true, width: 1280, height: 720)));
    }

    [Fact]
    public void ReusedDetachedDeviceNameIsSelectedAfterAdd()
    {
        // 本机现场：ADD 前 16 个 detached 适配器已在拓扑里，ADD 复用 DISPLAY5。
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DisplayInfo[] after =
        {
            Physical(@"\\.\DISPLAY1"),
            Parsec(@"\\.\DISPLAY5", attached: true, width: 1280, height: 720),
            Parsec(@"\\.\DISPLAY6"),
        };

        DisplayInfo? picked = DisplayTopology.PickNewLiveParsec(after, before);

        Assert.NotNull(picked);
        Assert.Equal(@"\\.\DISPLAY5", picked.DeviceName);
    }

    [Fact]
    public void AlreadyLiveDisplayIsNotTreatedAsNew()
    {
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"\\.\DISPLAY5" };
        DisplayInfo[] after =
        {
            Parsec(@"\\.\DISPLAY5", attached: true, width: 1280, height: 720),
            Parsec(@"\\.\DISPLAY6", attached: true, width: 1280, height: 720),
        };

        DisplayInfo? picked = DisplayTopology.PickNewLiveParsec(after, before);

        Assert.NotNull(picked);
        Assert.Equal(@"\\.\DISPLAY6", picked.DeviceName);
    }

    [Fact]
    public void DisplayOwnedByOtherProfileIsSkipped()
    {
        var before = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DisplayInfo[] after =
        {
            Parsec(@"\\.\DISPLAY5", attached: true, width: 1280, height: 720),
            Parsec(@"\\.\DISPLAY6", attached: true, width: 1280, height: 720),
        };

        DisplayInfo? picked = DisplayTopology.PickNewLiveParsec(
            after,
            before,
            device => device == @"\\.\DISPLAY5");

        Assert.NotNull(picked);
        Assert.Equal(@"\\.\DISPLAY6", picked.DeviceName);

        Assert.Null(DisplayTopology.PickNewLiveParsec(
            new[] { Parsec(@"\\.\DISPLAY5", attached: true, width: 1280, height: 720) },
            before,
            _ => true));
    }

    [Fact]
    public void DetachedAdaptersAndPhysicalDisplaysAreNeverPicked()
    {
        DisplayInfo[] displays =
        {
            Physical(@"\\.\DISPLAY1"),
            Parsec(@"\\.\DISPLAY5"),
            Parsec(@"\\.\DISPLAY6"),
        };

        Assert.Null(DisplayTopology.PickNewLiveParsec(
            displays,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
    }
}
