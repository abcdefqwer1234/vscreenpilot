using VScreenPilot.Errors;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ForegroundLeaseTests
{
    [Fact]
    public void AcquireRenewAndRelease()
    {
        var lease = new ForegroundLease();

        var acquired = lease.Acquire(
            "c0", 100, 0x10, 30_000, exclusive: false, "lease_input", (IntPtr)0x99);

        Assert.True(acquired.Active);
        Assert.Equal(100, acquired.Pid);
        Assert.True(lease.IsActive("c0"));
        Assert.True(lease.IsActiveForPid(100));

        var renewed = lease.Renew("c0", 60_000);
        Assert.NotNull(renewed);
        Assert.True(renewed!.ExpiresInMs > 30_000);

        (var released, IntPtr previous) = lease.Release("c0");
        Assert.NotNull(released);
        Assert.False(released!.Active);
        Assert.Equal(0x99, previous.ToInt64());
        Assert.False(lease.IsActive("c0"));
    }

    [Fact]
    public void DifferentClientDifferentPidConflicts()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c0", 100, 0x10, 30_000, false, "lease_input", IntPtr.Zero);

        ToolException error = Assert.Throws<ToolException>(() =>
            lease.Acquire("c1", 200, 0x20, 30_000, false, "lease_input", IntPtr.Zero));

        Assert.Equal("E_INPUT_LEASE_CONFLICT", error.Code);
    }

    [Fact]
    public void ForcePreemptsOtherClientLease()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c0", 100, 0x10, 30_000, false, "lease_input", IntPtr.Zero);

        var forced = lease.Acquire(
            "c1", 200, 0x20, 30_000, false, "lease_input", IntPtr.Zero, force: true);

        Assert.True(forced.Active);
        Assert.False(lease.IsActive("c0"));
        Assert.True(lease.IsActive("c1"));
    }

    [Fact]
    public void ForcePreemptKeepsPreviousForegroundForLaterRelease()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c0", 100, 0x10, 30_000, false, "lease_input", (IntPtr)0x99);

        lease.Acquire("c1", 200, 0x20, 30_000, false, "lease_input", IntPtr.Zero, force: true);

        (InputLeaseView? view, IntPtr previous) = lease.Release("c0");

        Assert.Null(view);
        Assert.Equal(0x99, previous.ToInt64());
    }

    [Fact]
    public void IsAnyActiveSeesOtherClients()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c1", 100, 0x10, 30_000, false, "lease_input", IntPtr.Zero);

        Assert.True(lease.IsAnyActive());
        lease.Release("c1");
        Assert.False(lease.IsAnyActive());
    }

    [Fact]
    public void RenewPreservesOrUpdatesExclusive()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c0", 100, 0x10, 30_000, exclusive: true, "lease_input", IntPtr.Zero);

        var renewed = lease.Renew("c0", 60_000);
        Assert.True(renewed!.Exclusive);

        var disabled = lease.Renew("c0", 60_000, exclusive: false);
        Assert.False(disabled!.Exclusive);
    }

    [Fact]
    public void SamePidFromAnotherClientRenewsWithoutConflict()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c0", 100, 0x10, 30_000, false, "lease_input", IntPtr.Zero);

        var other = lease.Acquire("c1", 100, 0x10, 30_000, false, "lease_input", IntPtr.Zero);

        Assert.True(other.Active);
        Assert.True(lease.IsActive("c0"));
        Assert.True(lease.IsActive("c1"));
    }

    [Fact]
    public void ClientDropRemovesLease()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c0", 100, 0x10, 30_000, false, "lease_input", (IntPtr)0x77);

        IntPtr previous = lease.OnClientDrop("c0");

        Assert.Equal(0x77, previous.ToInt64());
        Assert.False(lease.IsActive("c0"));
    }

    [Fact]
    public void ClearAllReturnsEarliestPreviousForeground()
    {
        var lease = new ForegroundLease();
        lease.Acquire("c0", 100, 0x10, 30_000, false, "lease_input", (IntPtr)0x99);
        lease.Acquire("c1", 100, 0x10, 30_000, false, "lease_input", (IntPtr)0x88);

        Assert.Equal(0x99, lease.ClearAll().ToInt64());
        Assert.False(lease.IsAnyActive());
    }

    [Fact]
    public void PreemptedPreviousExpires()
    {
        long now = 1_000;
        var lease = new ForegroundLease(() => now);
        lease.Acquire("c0", 100, 0x10, 30_000, false, "lease_input", (IntPtr)0x99);
        lease.Acquire("c1", 200, 0x20, 30_000, false, "lease_input", (IntPtr)0x77, force: true);

        now += ForegroundLease.PendingRestoreTtlMs + 1;
        (InputLeaseView? view, IntPtr previous) = lease.Release("c0");

        Assert.Null(view);
        Assert.Equal(0, previous.ToInt64());
    }

    [Fact]
    public void DeferredRestoreIsUsedOnlyWhenNoFallbackExists()
    {
        var lease = new ForegroundLease();
        lease.DeferRestore((IntPtr)0x55);

        Assert.Equal(0x77, lease.TakeRestoreTarget((IntPtr)0x77).ToInt64());

        // 上一次 Take 已清空暂存，不能再次恢复旧目标。
        Assert.Equal(0, lease.TakeRestoreTarget(IntPtr.Zero).ToInt64());

        lease.DeferRestore((IntPtr)0x66);
        Assert.Equal(0x66, lease.TakeRestoreTarget(IntPtr.Zero).ToInt64());
    }

    [Fact]
    public void DeferredRestoreExpires()
    {
        long now = 1_000;
        var lease = new ForegroundLease(() => now);
        lease.DeferRestore((IntPtr)0x55);

        now += ForegroundLease.PendingRestoreTtlMs + 1;

        Assert.Equal(0, lease.TakeRestoreTarget(IntPtr.Zero).ToInt64());
    }
}
