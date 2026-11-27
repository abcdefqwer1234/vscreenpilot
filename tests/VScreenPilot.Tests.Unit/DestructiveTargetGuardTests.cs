using VScreenPilot.Errors;
using VScreenPilot.Session;
using VScreenPilot.Uia;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class DestructiveTargetGuardTests
{
    private static UiaElementInfo Element(
        string name, string automationId, string controlType, int right = 100) =>
        new(
            "rt", name, automationId, 50000, controlType,
            0, 0, right, 30, true, false, 1, string.Empty);

    [Fact]
    public void CloseButtonIsBlockedByDefault()
    {
        UiaElementInfo info = Element("Close", "CloseButton", "Button");
        ToolException error = Assert.Throws<ToolException>(() =>
            DestructiveTargetGuard.Check(info, 50, 15, allowDestructive: false, "click"));

        Assert.Equal("E_DESTRUCTIVE_TARGET", error.Code);
    }

    [Fact]
    public void CloseAutomationIdIsBlocked()
    {
        UiaElementInfo info = Element("OK", "btnClose", "Button");
        ToolException error = Assert.Throws<ToolException>(() =>
            DestructiveTargetGuard.Check(info, 50, 15, allowDestructive: false, "click"));

        Assert.Equal("E_DESTRUCTIVE_TARGET", error.Code);
    }

    [Fact]
    public void AllowDestructiveReturnsHitView()
    {
        UiaElementInfo info = Element("删除", "DeleteButton", "Button");
        var view = DestructiveTargetGuard.Check(info, 50, 15, allowDestructive: true, "click");

        Assert.NotNull(view);
        Assert.True(view!.Destructive);
        Assert.Equal("删除", view.Name);
    }

    [Fact]
    public void SaveButtonIsAllowedUnderStandardPolicy()
    {
        UiaElementInfo info = Element("Save", "SaveButton", "Button");
        var view = DestructiveTargetGuard.Check(info, 50, 15, allowDestructive: false, "click");

        Assert.NotNull(view);
        Assert.False(view!.Destructive);
    }

    [Fact]
    public void TabRightEdgeIsTreatedAsCloseHotzone()
    {
        UiaElementInfo info = Element("画面: 3 - 图案", string.Empty, "TabItem", right: 200);

        Assert.True(
            DestructiveTargetGuard.IsDestructive(
                info.Name, info.AutomationId, info.ControlTypeName,
                info.Left, info.Top, info.Right, info.Bottom,
                195, 15, out string reason, out _));
        Assert.Equal("tab_close_hotzone", reason);

        Assert.False(
            DestructiveTargetGuard.IsDestructive(
                info.Name, info.AutomationId, info.ControlTypeName,
                info.Left, info.Top, info.Right, info.Bottom,
                100, 15, out _, out _));
    }

    [Fact]
    public void NarrowTabUsesFifteenPercentHotzone()
    {
        UiaElementInfo info = Element("画面: 3", string.Empty, "TabItem", right: 120);

        Assert.True(
            DestructiveTargetGuard.IsDestructive(
                info.Name, info.AutomationId, info.ControlTypeName,
                info.Left, info.Top, info.Right, info.Bottom,
                104, 15, out string reason, out _));
        Assert.Equal("tab_close_hotzone", reason);
    }

    [Fact]
    public void OffPolicyNeverBlocks()
    {
        UiaElementInfo info = Element("Close", "CloseButton", "Button");
        var view = DestructiveTargetGuard.Check(
            info, 50, 15, allowDestructive: false, "click", DestructiveTargetGuard.Policy.Off);

        Assert.NotNull(view);
        Assert.True(view!.Destructive);
    }
}
