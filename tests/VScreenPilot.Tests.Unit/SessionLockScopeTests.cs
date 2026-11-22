using VScreenPilot.Services;
using VScreenPilot.Tools;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>
/// release_environment 同时声明 sessionRead/sessionWrite 时，必须先拿写锁；
/// 读锁走带超时的 TryEnterRead，避免 release 期间无限阻塞。
/// </summary>
public sealed class SessionLockScopeTests
{
    [Fact]
    public void WriteFlagWinsOverRead()
    {
        var gate = new FakeGate();

        SessionLockScope.Acquisition acquisition =
            SessionLockScope.Acquire(gate, sessionRead: true, sessionWrite: true);

        Assert.Null(acquisition.Busy);
        Assert.NotNull(acquisition.Scope);
        Assert.Equal(["TryEnterExclusive(3000)"], gate.Calls);

        acquisition.Scope!.Dispose();

        Assert.Equal(["TryEnterExclusive(3000)", "ExitExclusive"], gate.Calls);
    }

    [Fact]
    public void ReadFlagUsesBoundedRead()
    {
        var gate = new FakeGate();

        SessionLockScope.Acquisition acquisition =
            SessionLockScope.Acquire(gate, sessionRead: true, sessionWrite: false);

        Assert.Null(acquisition.Busy);
        Assert.Equal(["TryEnterRead(10000)"], gate.Calls);

        acquisition.Scope!.Dispose();

        Assert.Equal(["TryEnterRead(10000)", "ExitRead"], gate.Calls);
    }

    [Fact]
    public void ExclusiveTimeoutReturnsBusyWithoutScope()
    {
        var gate = new FakeGate { ExclusiveResult = false };

        SessionLockScope.Acquisition acquisition =
            SessionLockScope.Acquire(gate, sessionRead: true, sessionWrite: true);

        Assert.Null(acquisition.Scope);
        Assert.Equal("E_BUSY", Code(acquisition.Busy));
        Assert.Equal(["TryEnterExclusive(3000)"], gate.Calls);
    }

    [Fact]
    public void ReadTimeoutReturnsBusyWithoutScope()
    {
        var gate = new FakeGate { ReadResult = false };

        SessionLockScope.Acquisition acquisition =
            SessionLockScope.Acquire(gate, sessionRead: true, sessionWrite: false);

        Assert.Null(acquisition.Scope);
        Assert.Equal("E_BUSY", Code(acquisition.Busy));
        Assert.Equal(["TryEnterRead(10000)"], gate.Calls);
    }

    [Fact]
    public void NoSessionFlagsAcquireNothing()
    {
        var gate = new FakeGate();

        SessionLockScope.Acquisition acquisition =
            SessionLockScope.Acquire(gate, sessionRead: false, sessionWrite: false);

        Assert.Null(acquisition.Scope);
        Assert.Null(acquisition.Busy);
        Assert.Empty(gate.Calls);
    }

    [Fact]
    public void ScopeReleasesOnlyOnce()
    {
        var gate = new FakeGate();
        SessionLockScope.Acquisition acquisition =
            SessionLockScope.Acquire(gate, sessionRead: false, sessionWrite: true);

        acquisition.Scope!.Dispose();
        acquisition.Scope!.Dispose();

        Assert.Equal(["TryEnterExclusive(3000)", "ExitExclusive"], gate.Calls);
    }

    private static string? Code(ToolResponse? response) =>
        response?.StructuredContent?["code"]?.GetValue<string>();

    private sealed class FakeGate : ISessionGate
    {
        public List<string> Calls { get; } = new();

        public bool ExclusiveResult { get; init; } = true;

        public bool ReadResult { get; init; } = true;

        public bool TryEnterRead(int timeoutMs)
        {
            Calls.Add($"TryEnterRead({timeoutMs})");
            return ReadResult;
        }

        public bool TryEnterExclusive(int timeoutMs)
        {
            Calls.Add($"TryEnterExclusive({timeoutMs})");
            return ExclusiveResult;
        }

        public void ExitRead() => Calls.Add("ExitRead");

        public void ExitExclusive() => Calls.Add("ExitExclusive");
    }
}
