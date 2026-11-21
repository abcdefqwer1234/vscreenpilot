using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Apps;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager : ISnapshotHost
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

    UiaHost ISnapshotHost.Uia => Uia;

    ElementRefStore ISnapshotHost.Refs => _refs;

    string ISnapshotHost.SnapshotId => SnapshotId;

    MetricsRegistry ISnapshotHost.Metrics => Metrics;

    int ISnapshotHost.RefCapacity => ElementRefCapacity;

    DisplayInfo ISnapshotHost.EnsureDisplay() => EnsureEnvironmentFast();

    void ISnapshotHost.PruneApps() => PruneDeadApps();

    TrackedApp? ISnapshotHost.FindApp(int? pid) => FindLiveTrackedApp(pid);

    void ISnapshotHost.RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label) =>
        RequireWindowOnDisplay(display, hwnd, label);

    Func<bool> ISnapshotHost.CancellationProbe() => CancellationProbe();

    string ISnapshotHost.BeginSnapshot() => BeginSnapshot();

    string ISnapshotHost.RegisterElement(UiaElementInfo info, IntPtr hwnd) =>
        RegisterElement(info, hwnd);

    IReadOnlyList<TrackedApp> ISnapshotHost.OwnedApps() => _apps.Where(IsOwned);

    bool ISnapshotHost.IsLive(TrackedApp app) => TrackedAppTracker.IsLive(app);

    (byte[] Bgra, int Width, int Height) ISnapshotHost.CaptureRegion(
        int x, int y, int width, int height) => CaptureRegion(x, y, width, height);

    CaptureResult ISnapshotHost.CaptureForSnapshot(double scale, double previewScale) =>
        CaptureScreenEx(
            null, null, null, null, scale, savePath: null,
            storeFrame: false, previewScale: previewScale);
}
