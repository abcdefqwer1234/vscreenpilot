using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>
/// 共享进程的强杀条件：只有专用进程，或“本会话启动且确认没有其他窗口”时才允许。
/// hasOtherWindows=true（含枚举失败的 fail-closed 值）绝不能强杀。
/// </summary>
public sealed class AppTerminationTests
{
    [Theory]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    public void CanForceCloseTruthTable(
        bool dedicated, bool started, bool hasOtherWindows, bool expected)
    {
        Assert.Equal(expected, AppTermination.CanForceClose(dedicated, started, hasOtherWindows));
    }
}
