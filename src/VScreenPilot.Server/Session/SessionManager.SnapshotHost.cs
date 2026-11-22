using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Apps;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    private readonly SnapshotBuilder _snapshots;

    public SnapshotResult Snapshot(
        int? pid,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        bool includeValues,
        bool includeScreenshot,
        double scale,
        bool annotate = false) =>
        _snapshots.Snapshot(
            pid, maxDepth, interactiveOnly, maxNodes, includeValues, includeScreenshot, scale, annotate);
}
