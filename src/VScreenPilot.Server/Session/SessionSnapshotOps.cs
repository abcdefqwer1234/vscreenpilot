using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Apps;

namespace VScreenPilot.Session;

/// <summary>SnapshotOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionSnapshotOps
{
    internal readonly SessionManager _host;

    public SessionSnapshotOps(SessionManager host) => _host = host;

    public SnapshotResult Snapshot(
        int? pid,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        bool includeValues,
        bool includeScreenshot,
        double scale,
        bool annotate = false) =>
        _host.Snapshots.Snapshot(
            pid, maxDepth, interactiveOnly, maxNodes, includeValues, includeScreenshot, scale, annotate);
}
