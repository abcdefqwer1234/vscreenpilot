using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>
/// IPC/CLI 只需要的最小会话门面：查询、快照、采集、环境、释放。
/// <see cref="IDisposable.Dispose"/> 会释放整个会话，只能由会话的所有者调用；
/// 当前仅 CLI 入口（<c>LocalSessionInvoker</c>）这样做，daemon 路径由 <c>ServerHost</c> 负责。
/// </summary>
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
