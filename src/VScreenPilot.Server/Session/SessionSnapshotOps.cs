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

    public SnapshotResult Snapshot(UiSnapshotRequest request) =>
        _host.Snapshots.Snapshot(request);
}
