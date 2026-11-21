using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>崩溃恢复用的会话状态持久化。</summary>
public sealed class SessionStateKeeperTests
{
    [Fact]
    public void SavePersistsDisplayGeometryAndProcessIdentity()
    {
        var store = new FakeStore();
        var host = new FakeHost(store);
        host.DisplayState.Index = 3;
        host.DisplayState.VirtualDisplay = new DisplayInfo(
            @"\\.\DISPLAY9", "Parsec Virtual Display Adapter", "VDD", Attached: true,
            Primary: false, X: 2720, Y: 0, Width: 1280, Height: 720, Frequency: 60, Dpi: 96);
        var keeper = new SessionStateKeeper(host);

        keeper.Save();

        SessionState state = Assert.IsType<SessionState>(store.State);
        Assert.Equal(Environment.ProcessId, state.Pid);
        Assert.Equal(3, state.DisplayIndex);
        Assert.Equal(@"\\.\DISPLAY9", state.DisplayDevice);
        Assert.Equal(2720, state.X);
        Assert.Equal(0, state.Y);
        Assert.Equal(1280, state.Width);
        Assert.Equal(720, state.Height);
        Assert.Empty(state.Apps);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public void RecoverRestoresPreviousSessionPointers()
    {
        var store = new FakeStore
        {
            State = new SessionState
            {
                Pid = 999_999,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-2).ToString("O"),
                DisplayIndex = 5,
                DisplayDevice = @"\\.\DISPLAY9",
            },
        };
        var host = new FakeHost(store);
        var keeper = new SessionStateKeeper(host);

        keeper.Recover();

        Assert.Equal(999_999, host.DisplayState.RecoveredPid);
        Assert.Equal(5, host.DisplayState.RecoveredIndex);
        Assert.Equal(@"\\.\DISPLAY9", host.DisplayState.RecoveredDevice);
    }

    [Fact]
    public void RecoverSkipsWhenPreviousProcessStillAlive()
    {
        var store = new FakeStore
        {
            State = new SessionState
            {
                Pid = Environment.ProcessId,
                StartedAt = EnvironmentProbe.ProcessStartTime(),
                DisplayIndex = 5,
                DisplayDevice = @"\\.\DISPLAY9",
            },
        };
        var host = new FakeHost(store);
        var keeper = new SessionStateKeeper(host);

        keeper.Recover();

        Assert.Equal(-1, host.DisplayState.RecoveredIndex);
        Assert.Equal(string.Empty, host.DisplayState.RecoveredDevice);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsVScreenPilotProcessMatchesPidAndStartTime(bool correctStartTime)
    {
        string startedAt = correctStartTime
            ? EnvironmentProbe.ProcessStartTime()
            : DateTimeOffset.UtcNow.AddDays(-1).ToString("O");

        bool actual = SessionStateKeeper.IsVScreenPilotProcess(Environment.ProcessId, startedAt);

        Assert.Equal(correctStartTime, actual);
    }

    [Fact]
    public void IsVScreenPilotProcessRejectsUnknownPid()
    {
        Assert.False(SessionStateKeeper.IsVScreenPilotProcess(0, string.Empty));
    }

    private sealed class FakeStore : ISessionStateStore
    {
        public SessionState? State { get; set; }

        public int SaveCount { get; private set; }

        public int LoadCount { get; private set; }

        public int ClearCount { get; private set; }

        public SessionState? Load()
        {
            LoadCount++;
            return State;
        }

        public void Save(SessionState state)
        {
            SaveCount++;
            State = state;
        }

        public void Clear()
        {
            ClearCount++;
            State = null;
        }
    }

    private sealed class FakeHost : ISessionStateHost
    {
        public FakeHost(ISessionStateStore store)
        {
            Store = store;
            Apps = new TrackedAppRegistry(Gate);
        }

        public object Gate { get; } = new();

        public ISessionStateStore Store { get; }

        public DisplayStateStore DisplayState { get; } = new();

        public TrackedAppRegistry Apps { get; }

        public LogRing Log { get; } = new(capacity: 10, fileEnabled: false);
    }
}
