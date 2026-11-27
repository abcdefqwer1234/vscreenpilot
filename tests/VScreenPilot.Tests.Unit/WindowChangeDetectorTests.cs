using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class WindowChangeDetectorTests
{
    [Fact]
    public void FindsFirstNewVisibleNonMenuWindow()
    {
        var before = new HashSet<IntPtr> { (IntPtr)1 };
        var windows = new[]
        {
            ((IntPtr)1, "main", "#32770", true, 100L),
            ((IntPtr)2, "tooltip", "#32768", true, 10L),
            ((IntPtr)3, "状态", "#32770", true, 200L),
        };

        WindowChange? change = WindowChangeDetector.FindNew(before, windows);

        Assert.NotNull(change);
        Assert.Equal(3, change!.Hwnd.ToInt64());
        Assert.Equal("状态", change.Title);
    }

    [Fact]
    public void IgnoresTooltipAndImeWindows()
    {
        var before = new HashSet<IntPtr>();
        var windows = new[]
        {
            ((IntPtr)1, "tip", "tooltips_class32", true, 100L),
            ((IntPtr)2, "ime", "IME", true, 100L),
            ((IntPtr)3, "dialog", "#32770", true, 100L),
        };

        WindowChange? change = WindowChangeDetector.FindNew(before, windows);

        Assert.NotNull(change);
        Assert.Equal(3, change!.Hwnd.ToInt64());
    }

    [Fact]
    public void IgnoresHiddenAndZeroAreaWindows()
    {
        var before = new HashSet<IntPtr>();
        var windows = new[]
        {
            ((IntPtr)2, "hidden", "#32770", false, 100L),
            ((IntPtr)3, "zero", "#32770", true, 0L),
        };

        Assert.Null(WindowChangeDetector.FindNew(before, windows));
    }
}
