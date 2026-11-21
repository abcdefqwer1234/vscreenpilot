using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class LaunchPolicyTests
{
    [Fact]
    public void AdoptedWindowIsNeverClosedOnRelease()
    {
        // 单实例回退：close_on_release 必须保持 false，否则 release 会关闭用户原有窗口。
        (bool closeOnRelease, bool dedicated) =
            AppLauncher.ResolveNewInstanceOwnership(
                adoptedExisting: true, hadRunningInstance: true, isEdge: false);

        Assert.False(closeOnRelease);
        Assert.False(dedicated);
    }

    [Fact]
    public void FreshInstanceIsOwnedAndClosed()
    {
        (bool closeOnRelease, bool dedicated) =
            AppLauncher.ResolveNewInstanceOwnership(
                adoptedExisting: false, hadRunningInstance: false, isEdge: false);

        Assert.True(closeOnRelease);
        Assert.True(dedicated);
    }

    [Fact]
    public void EdgeKeepsDedicatedProfileWhenAnotherInstanceIsRunning()
    {
        (bool closeOnRelease, bool dedicated) =
            AppLauncher.ResolveNewInstanceOwnership(
                adoptedExisting: false, hadRunningInstance: true, isEdge: true);

        Assert.True(closeOnRelease);
        Assert.True(dedicated);
    }

    [Theory]
    [InlineData("notepad", true, null, "*note.txt - 记事本", true)]
    [InlineData("notepad", true, null, "无标题 - 记事本", false)]
    [InlineData("notepad", true, null, "Untitled - Notepad", false)]
    [InlineData("notepad", true, "a.txt", "a.txt - 记事本", false)]
    [InlineData("notepad", false, null, "note.txt - 记事本", false)]
    [InlineData("winword", true, null, "note.txt - Word", false)]
    [InlineData("notepad", true, null, "", false)]
    public void RestoredNotepadTabIsDetectedOnlyForFreshNoArgLaunch(
        string exeName, bool started, string? arguments, string title, bool expected)
    {
        Assert.Equal(
            expected,
            AppLauncher.LooksLikeRestoredNotepadTitle(exeName, started, arguments, title));
    }
}
