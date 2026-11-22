using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>IPC/CLI 只需要的最小会话门面：查询、快照、采集、环境、释放。</summary>
internal interface ISessionFacade : IDisposable
{
    string ProfileName { get; }
    bool Initialized { get; }
    string ArtifactDirectory { get; }
    SnapshotResult Snapshot(SnapshotRequest request);
    CaptureResult CaptureScreen(CaptureRequest request);
    EnvironmentView GetEnvironment(bool ensure = false);
    IReadOnlyList<TrackedApp> GetApps();
    MemoryView TrimIdleMemory(string reason);
}
