using VScreenPilot.Errors;
using VScreenPilot.Mcp;
using VScreenPilot.Server.Infrastructure;
using Xunit;

using VScreenPilot.Tools;

namespace VScreenPilot.Tests;

/// <summary>
/// 回归：BeginInvocation 先拿屏幕租约、后排队写锁；写锁失败或取消时，
/// 已拿到的屏幕租约必须回滚，否则 holder 会泄漏到 TTL 到期。
/// </summary>
public sealed class ToolExecutorLeaseTests
{
    [Fact]
    public async Task WriteLockBusyReleasesScreenLease()
    {
        var screen = new ScreenLease(60_000, 0);
        using var scheduler = new WriteScheduler();
        using WriteScheduler.Lease holder = await scheduler.AcquireAsync(
            "holder", TimeSpan.FromSeconds(1), CancellationToken.None);

        ToolException exception = await Assert.ThrowsAsync<ToolException>(() =>
            InvocationGate.AcquireAsync(
                screen,
                scheduler,
                "c1",
                "click",
                requiresScreenLease: true,
                mutates: true,
                writeTimeout: TimeSpan.FromMilliseconds(1),
                bypassWriteLock: false,
                cancellationToken: CancellationToken.None));

        Assert.Equal("E_BUSY", exception.Code);
        AssertNoActiveCalls(screen);
    }

    [Fact]
    public async Task CancellationReleasesScreenLease()
    {
        var screen = new ScreenLease(60_000, 0);
        using var scheduler = new WriteScheduler();
        using WriteScheduler.Lease holder = await scheduler.AcquireAsync(
            "holder", TimeSpan.FromSeconds(1), CancellationToken.None);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvocationGate.AcquireAsync(
                screen,
                scheduler,
                "c1",
                "click",
                requiresScreenLease: true,
                mutates: true,
                writeTimeout: TimeSpan.FromSeconds(1),
                bypassWriteLock: false,
                cancellationToken: cts.Token));

        AssertNoActiveCalls(screen);
    }

    [Fact]
    public async Task ScreenLeaseBusyDoesNotReleaseExistingHolder()
    {
        var screen = new ScreenLease(60_000, 0);
        using IDisposable first = screen.Acquire("c1", "snapshot");
        using var scheduler = new WriteScheduler();

        ToolException exception = await Assert.ThrowsAsync<ToolException>(() =>
            InvocationGate.AcquireAsync(
                screen,
                scheduler,
                "c2",
                "click",
                requiresScreenLease: true,
                mutates: true,
                writeTimeout: TimeSpan.FromSeconds(1),
                bypassWriteLock: false,
                cancellationToken: CancellationToken.None));

        Assert.Equal("E_SCREEN_LEASED", exception.Code);
        Assert.Equal("c1", screen.HolderId);
        Assert.Equal(1, screen.Describe()["active_calls"]!.GetValue<int>());
    }

    [Fact]
    public async Task SuccessHoldsBothUntilDisposed()
    {
        var screen = new ScreenLease(60_000, 0);
        using var scheduler = new WriteScheduler();

        (WriteScheduler.Lease? lease, IDisposable? screenLease) = await InvocationGate.AcquireAsync(
            screen,
            scheduler,
            "c1",
            "click",
            requiresScreenLease: true,
            mutates: true,
            writeTimeout: TimeSpan.FromSeconds(1),
            bypassWriteLock: false,
            cancellationToken: CancellationToken.None);

        using (lease)
        using (screenLease)
        {
            Assert.NotNull(lease);
            Assert.NotNull(screenLease);
            Assert.True(screen.Describe()["held"]!.GetValue<bool>());
            Assert.Equal(1, screen.Describe()["active_calls"]!.GetValue<int>());
            Assert.Equal("click", scheduler.Holder);
        }

        AssertNoActiveCalls(screen);
        Assert.Null(scheduler.Holder);
    }

    private static void AssertNoActiveCalls(ScreenLease screen)
    {
        // 客户端仍可继续持有屏幕租约（直到 release_environment 或 TTL），
        // 但失败/完成后的在途调用计数必须归零。
        Assert.Equal(0, screen.Describe()["active_calls"]!.GetValue<int>());
    }
}
