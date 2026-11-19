using VScreenPilot.Mcp;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ScreenLeaseTests
{
    [Fact]
    public void SecondClientIsRejectedUntilHolderReleases()
    {
        var lease = new ScreenLease();

        using (IDisposable first = lease.Acquire("c1", "type_text"))
        {
            ToolException exception = Assert.Throws<ToolException>(() => lease.Acquire("c2", "click"));
            Assert.Equal("E_SCREEN_LEASED", exception.Code);
        }

        // 同一客户端空闲后仍持有 TTL 租约；显式 release 才转给下一个客户端。
        lease.ReleaseHolder("c1");
        using IDisposable second = lease.Acquire("c2", "click");
        Assert.True(lease.Describe()["held"]?.GetValue<bool>());
    }

    [Fact]
    public void DescribeReportsHolderAndActiveCalls()
    {
        var lease = new ScreenLease();
        using IDisposable held = lease.Acquire("c9", "capture_screen");

        var json = lease.Describe();
        Assert.True(json["held"]?.GetValue<bool>());
        Assert.Equal("c9", json["holder_client_id"]?.GetValue<string>());
        Assert.Equal(1, json["active_calls"]?.GetValue<int>());
    }

    [Fact]
    public void WaitAcquiresAfterHolderReleases()
    {
        var lease = new ScreenLease(60_000, 1_000);

        using (IDisposable first = lease.Acquire("c1", "type_text"))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(100);
                first.Dispose();
                lease.ReleaseHolder("c1");
            });

            using IDisposable second = lease.Acquire("c2", "click");
            Assert.Equal("c2", lease.HolderId);
        }
    }

    [Fact]
    public void WaitTimeoutReportsWaitedMs()
    {
        var lease = new ScreenLease(60_000, 100);
        using IDisposable first = lease.Acquire("c1", "type_text");
        ToolException exception = Assert.Throws<ToolException>(
            () => lease.Acquire("c2", "click"));

        Assert.Equal("E_SCREEN_LEASED", exception.Code);
        Assert.Contains("waited_ms", exception.Details!.ToJsonString());
    }

    [Fact]
    public void ExtendPreventsIdleSteal()
    {
        var lease = new ScreenLease(1_000, 0);
        using IDisposable first = lease.Acquire("c1", "type_text");
        Assert.True(lease.Extend("c1", 5_000));
        first.Dispose();

        Thread.Sleep(1_200);
        ToolException exception = Assert.Throws<ToolException>(
            () => lease.Acquire("c2", "click"));
        Assert.Equal("E_SCREEN_LEASED", exception.Code);
    }

    [Fact]
    public void IdleStealIsLogged()
    {
        var messages = new List<string>();
        var lease = new ScreenLease(1_000, 0);
        lease.LogSink = messages.Add;

        try
        {

            using (IDisposable first = lease.Acquire("c1", "type_text"))
            {
                first.Dispose();
                Thread.Sleep(1_200);
                using IDisposable second = lease.Acquire("c2", "click");
                Assert.Equal("c2", lease.HolderId);
            }

            Assert.Contains(messages, message => message.Contains("screen_lease_stolen"));
        }
        finally
        {
            lease.LogSink = null;
        }
    }

    [Fact]
    public void ForceReleaseFreesLeaseWhileCallIsInFlight()
    {
        var lease = new ScreenLease(60_000, 0);
        IDisposable first = lease.Acquire("c1", "release_environment");

        // release_environment 自身持有一个租约，IfIdle 会拒绝；Force 必须成功。
        Assert.False(lease.ReleaseHolderIfIdle("c1"));
        Assert.True(lease.ForceRelease("c1"));
        Assert.Null(lease.HolderId);

        using IDisposable second = lease.Acquire("c2", "click");
        Assert.Equal("c2", lease.HolderId);

        // 旧持有者的迟到 Dispose 不能递减新持有者的 active_calls。
        first.Dispose();
        Assert.Equal("c2", lease.HolderId);
        Assert.Equal(1, lease.Describe()["active_calls"]!.GetValue<int>());
    }

    [Fact]
    public void ForceReleaseOnlyAffectsOwnHolder()
    {
        var lease = new ScreenLease(60_000, 0);
        using IDisposable first = lease.Acquire("c1", "click");

        Assert.False(lease.ForceRelease("c2"));
        Assert.Equal("c1", lease.HolderId);
    }

    [Fact]
    public void DisconnectCleanupOnlyReportsFreeWhenNobodyIsOperating()
    {
        var lease = new ScreenLease();

        using (IDisposable first = lease.Acquire("c1", "type_text"))
        {
            // handler 仍在运行：不能把租约转给下一个客户端。
            Assert.False(lease.ReleaseHolderIfIdle("c1"));
            Assert.Equal("c1", lease.HolderId);
        }

        // handler 结束后，断开清理可以释放并确认无人持有。
        Assert.True(lease.ReleaseHolderIfIdle("c1"));
        Assert.Null(lease.HolderId);

        using IDisposable second = lease.Acquire("c2", "click");
        Assert.False(lease.ReleaseHolderIfIdle("c1"));
        Assert.Equal("c2", lease.HolderId);
    }
}
