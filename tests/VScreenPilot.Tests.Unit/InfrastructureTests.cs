using System.Runtime.InteropServices;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Mcp;
using Xunit;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Errors;

namespace VScreenPilot.Tests;

public sealed class ErrorCatalogTests
{
    [Fact]
    public void EveryCodeHasActionableMetadata()
    {
        Assert.NotEmpty(ErrorCatalog.All);

        foreach ((string code, ErrorInfo info) in ErrorCatalog.All)
        {
            Assert.StartsWith("E_", code);
            Assert.Equal(code, info.Code);
            Assert.False(string.IsNullOrWhiteSpace(info.Hint), code);
            Assert.False(string.IsNullOrWhiteSpace(info.Summary), code);
            Assert.False(string.IsNullOrWhiteSpace(info.Stage), code);
        }
    }

    [Fact]
    public void AggregateExceptionIsUnwrapped()
    {
        var inner = new ToolException("E_INVALID_ARG", "bad pid");
        ToolException normalized = ErrorCatalog.Normalize(new AggregateException(inner));
        Assert.Equal("E_INVALID_ARG", normalized.Code);
    }

    [Fact]
    public void MissingPathComBecomesAppGone()
    {
        // 测试必须构造真实的 COMException，才能验证 ErrorCatalog 的 HRESULT 映射。
#pragma warning disable CA2201
        var com = new COMException("gone", unchecked((int)0x80070003));
#pragma warning restore CA2201
        ToolException normalized = ErrorCatalog.Normalize(com);
        Assert.Equal("E_APP_GONE", normalized.Code);
    }

    [Fact]
    public void UnknownExceptionKeepsInternalButAddsHint()
    {
        ToolException normalized = ErrorCatalog.Normalize(new InvalidOperationException("boom"));
        Assert.Equal("E_INTERNAL", normalized.Code);
        Assert.False(string.IsNullOrWhiteSpace(normalized.Hint));
    }
}

public sealed class WriteSchedulerTests
{
    [Fact]
    public async Task SerializesWritersAndReturnsBusyMetadata()
    {
        using var scheduler = new WriteScheduler();
        using WriteScheduler.Lease first = await scheduler.AcquireAsync(
            "type_text", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        Assert.True(first.Held);
        Assert.Equal("type_text", scheduler.Holder);

        ToolException busy = await Assert.ThrowsAsync<ToolException>(() =>
            scheduler.AcquireAsync("click", TimeSpan.FromMilliseconds(50), CancellationToken.None));

        Assert.Equal("E_BUSY", busy.Code);
        Assert.NotNull(busy.Hint);
        Assert.Contains("retry_after_ms", busy.Details!.ToJsonString());
    }

    [Fact]
    public async Task ReleaseAllowsNextWriter()
    {
        using var scheduler = new WriteScheduler();

        using (await scheduler.AcquireAsync("a", TimeSpan.FromMilliseconds(50), CancellationToken.None))
        {
        }

        using WriteScheduler.Lease second = await scheduler.AcquireAsync(
            "b", TimeSpan.FromMilliseconds(50), CancellationToken.None);
        Assert.True(second.Held);
    }

    [Fact]
    public async Task HeldMsUsesMilliseconds()
    {
        using var scheduler = new WriteScheduler();
        using WriteScheduler.Lease lease = await scheduler.AcquireAsync(
            "type_text", TimeSpan.FromMilliseconds(50), CancellationToken.None);

        await Task.Delay(30);

        // 旧实现把毫秒当 ticks，held_ms 恒为 0。
        Assert.True(scheduler.HeldMs >= 10, $"held_ms={scheduler.HeldMs}");
    }

    [Fact]
    public async Task BypassReturnsUnheldLease()
    {
        // 生产默认 5s；测试用 10ms 验证“只有卡住的锁才旁路”。
        using var scheduler = new WriteScheduler(bypassAfterMs: 10);
        using WriteScheduler.Lease first = await scheduler.AcquireAsync(
            "release_environment", TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await Task.Delay(25);

        using WriteScheduler.Lease bypass = await scheduler.AcquireAsync(
            "release_environment", TimeSpan.FromMilliseconds(20), CancellationToken.None, bypass: true);

        Assert.True(first.Held);
        Assert.False(bypass.Held);
    }
}

[Collection("ProfileEnvironment")]
public sealed class ProfileTests
{
    [Fact]
    public void SanitizesProfileNames()
    {
        Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, " My Profile!! ");
        try
        {
            Assert.Equal("my-profile", Profile.Resolve());
            Assert.Equal("VScreenPilot_Session_my-profile", Profile.MutexName("my-profile"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, null);
        }
    }

    [Fact]
    public void DefaultProfileKeepsLegacyMutexName()
    {
        Assert.Equal("VScreenPilot_Session_0", Profile.MutexName("default"));
        Assert.Equal("state.json", Profile.StateFileName("default"));
    }
}

public sealed class JobObjectTests
{
    [Fact]
    public void ReportsCreationStatus()
    {
        using var job = new JobObject();
        Assert.True(job.IsValid);
        Assert.True(job.Status is "ready" or "limits_failed:5", job.Status);
    }
}
