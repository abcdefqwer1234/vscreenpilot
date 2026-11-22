using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>
/// 释放/回收的纯决策：强杀失败不删 profile、save 不走强杀、
/// release 有内部预算、孤儿窗口有宽限期。
/// </summary>
public sealed class ReleaseSafetyTests
{
    [Fact]
    public void TempProfileIsKeptWhileProcessAlive()
    {
        Assert.False(EnvironmentReleaser.CanDeleteTempProfile(reclaim: true, processAlive: true));
        Assert.True(EnvironmentReleaser.CanDeleteTempProfile(reclaim: true, processAlive: false));
        Assert.False(EnvironmentReleaser.CanDeleteTempProfile(reclaim: false, processAlive: false));
    }

    [Fact]
    public void SaveDialogFailureKeepsWindowButDiscardDoesNot()
    {
        Assert.Equal("kept", AppReleaser.SaveDialogUnhandledResult("save"));
        Assert.Null(AppReleaser.SaveDialogUnhandledResult("discard"));
        Assert.Null(AppReleaser.SaveDialogUnhandledResult("force"));
    }

    [Fact]
    public void ForcedCloseReportsOnlyRealKills()
    {
        Assert.Equal("forced", AppReleaser.ForcedCloseResult(killed: true, processAlive: true));
        Assert.Equal("kept", AppReleaser.ForcedCloseResult(killed: false, processAlive: true));
        Assert.Equal("closed", AppReleaser.ForcedCloseResult(killed: false, processAlive: false));
    }

    [Fact]
    public void ReleaseBudgetStopsOnlyWhenWorkRemains()
    {
        long budget = EnvironmentReleaser.ReleaseBudgetMs;

        Assert.False(EnvironmentReleaser.ReleaseBudgetExceeded(budget - 1, processed: 0, total: 3));
        Assert.True(EnvironmentReleaser.ReleaseBudgetExceeded(budget, processed: 1, total: 3));
        Assert.False(EnvironmentReleaser.ReleaseBudgetExceeded(budget, processed: 3, total: 3));
    }

    [Fact]
    public void OrphanReapRequiresGraceAndMissingClient()
    {
        Assert.False(SessionClientOps.ShouldReapOrphan(
            clientLive: true, orphanSinceTicks: 0, nowTicks: 60_000, graceMs: 10_000));
        Assert.False(SessionClientOps.ShouldReapOrphan(
            clientLive: false, orphanSinceTicks: 55_000, nowTicks: 60_000, graceMs: 10_000));
        Assert.True(SessionClientOps.ShouldReapOrphan(
            clientLive: false, orphanSinceTicks: 50_000, nowTicks: 60_000, graceMs: 10_000));
    }
}
