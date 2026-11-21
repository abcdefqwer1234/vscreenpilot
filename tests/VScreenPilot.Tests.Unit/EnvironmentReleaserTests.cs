using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Mcp;
using VScreenPilot.Session;
using VScreenPilot.Views;
using Xunit;
using VScreenPilot.Errors;

namespace VScreenPilot.Tests;

/// <summary>release_environment 的策略分支与清理动作。</summary>
public sealed class EnvironmentReleaserTests
{
    [Fact]
    public void InvalidOnDirtyIsRejectedBeforeEpochAdvances()
    {
        var host = new FakeHost();
        var releaser = new EnvironmentReleaser(host);

        ToolException exception = Assert.Throws<ToolException>(
            () => releaser.Release(keepDisplay: true, onDirty: "nonsense"));

        Assert.Equal("E_INVALID_ARG", exception.Code);
        Assert.Equal(0, host.EpochCount);
    }

    [Fact]
    public void ForceAndDiscardRequireDataLossConfirmation()
    {
        var host = new FakeHost();
        var releaser = new EnvironmentReleaser(host);

        foreach (string policy in new[] { "force", "discard" })
        {
            ToolException exception = Assert.Throws<ToolException>(
                () => releaser.Release(keepDisplay: true, onDirty: policy));
            Assert.Equal("E_DATA_LOSS_REQUIRED", exception.Code);
        }

        Assert.Equal(0, host.EpochCount);
    }

    [Fact]
    public void AbortForcesDisplayToStayAndSavesState()
    {
        var host = new FakeHost();
        var releaser = new EnvironmentReleaser(host);

        ReleaseReport report = releaser.Release(keepDisplay: false, onDirty: "abort");

        Assert.True(report.DisplayKept);
        Assert.False(report.DisplayRemoved);
        Assert.Equal(0, host.RemoveCalls);
        Assert.Equal(1, host.SaveCount);
        Assert.Equal(0, host.StateClearCount);
        Assert.Contains(report.Warnings, warning => warning.Contains("abort", StringComparison.Ordinal));
    }

    [Fact]
    public void OtherClientsKeepDisplayEvenWhenCallerAskedToRemove()
    {
        var host = new FakeHost { ActiveClientCount = 2 };
        var releaser = new EnvironmentReleaser(host);

        ReleaseReport report = releaser.Release(keepDisplay: false, onDirty: "save");

        Assert.True(report.DisplayKept);
        Assert.Equal(0, host.RemoveCalls);
        Assert.Equal(1, host.SaveCount);
        Assert.Contains(report.Warnings, warning => warning.Contains("其他客户端", StringComparison.Ordinal));
    }

    [Fact]
    public void SingleClientReleaseRemovesDisplayAndClearsPerClientState()
    {
        var host = new FakeHost();
        var releaser = new EnvironmentReleaser(host);

        ReleaseReport report = releaser.Release(keepDisplay: false, onDirty: "save");

        Assert.True(report.DisplayRemoved);
        Assert.False(report.DisplayKept);
        Assert.Equal(1, host.EpochCount);
        Assert.Equal(1, host.RemoveCalls);
        Assert.Equal(1, host.VddStopCount);
        Assert.Equal(1, host.StateClearCount);
        Assert.False(Assert.Single(host.InitializedStates));
        Assert.False(Assert.Single(host.ElementRefClears));
        Assert.False(Assert.Single(host.FrameClears));
        Assert.False(Assert.Single(host.InputClears));
        Assert.True(host.FocusResumed);
        Assert.Equal("release", Assert.Single(host.TrimReasons));
        Assert.Equal(0, host.SelfHealResets);
    }

    [Fact]
    public void ForceAllClientsClearsSharedStateAndResetsSelfHealFlag()
    {
        var host = new FakeHost();
        var releaser = new EnvironmentReleaser(host);

        ReleaseReport report = releaser.Release(
            keepDisplay: false, onDirty: "save", forceAllClients: true);

        Assert.True(report.DisplayRemoved);
        Assert.True(Assert.Single(host.ElementRefClears));
        Assert.True(Assert.Single(host.FrameClears));
        Assert.True(Assert.Single(host.InputClears));
        Assert.Equal(1, host.SelfHealResets);
    }

    [Fact]
    public void KeepingDisplaySavesStateAndMarksInitialized()
    {
        var host = new FakeHost();
        var releaser = new EnvironmentReleaser(host);

        ReleaseReport report = releaser.Release(keepDisplay: true, onDirty: "save");

        Assert.False(report.DisplayRemoved);
        Assert.True(report.DisplayKept);
        Assert.Equal(1, host.SaveCount);
        Assert.True(Assert.Single(host.InitializedStates));
        Assert.Equal(0, host.StateClearCount);
    }

    private sealed class FakeHost : IEnvironmentReleaserHost
    {
        public object Gate { get; } = new();

        public int EpochCount { get; private set; }

        public int ActiveClientCount { get; init; } = 1;

        public string CurrentClientId => "c1";

        public TrackedAppRegistry Apps { get; } = new(new object());

        public bool IsOwned(TrackedApp app) => true;

        public string CloseTrackedApp(TrackedApp app, string policy, out string? warning)
        {
            warning = null;
            return "closed";
        }

        public bool FocusResumed { get; private set; }

        public List<bool> ElementRefClears { get; } = new();

        public List<bool> FrameClears { get; } = new();

        public List<bool> InputClears { get; } = new();

        public int SelfHealResets { get; private set; }

        public List<bool> InitializedStates { get; } = new();

        public bool DisplayAttached => true;

        public int RemoveCalls { get; private set; }

        public int VddStopCount { get; private set; }

        public int StateClearCount { get; private set; }

        public int SaveCount { get; private set; }

        public List<string> TrimReasons { get; } = new();

        public LogRing Log { get; } = new(capacity: 10, fileEnabled: false);

        public void IncrementEpoch() => EpochCount++;

        public void ResumeFocusGuard() => FocusResumed = true;

        public void ClearElementRefs(bool allClients) => ElementRefClears.Add(allClients);

        public void ClearFrames(bool allClients) => FrameClears.Add(allClients);

        public void ClearInputBlock(bool allClients) => InputClears.Add(allClients);

        public void MarkDisplaySelfHealedFalse() => SelfHealResets++;

        public void MarkDisplayInitialized(bool initialized) => InitializedStates.Add(initialized);

        public bool TryRemoveDisplay()
        {
            RemoveCalls++;
            return true;
        }

        public void StopAndDisposeVdd() => VddStopCount++;

        public void ClearStateStore() => StateClearCount++;

        public void SaveState() => SaveCount++;

        public MemoryView TrimIdleMemory(string reason)
        {
            TrimReasons.Add(reason);
            return new MemoryView(10, 10, 10, Trimmed: false, Emptied: false);
        }
    }
}
