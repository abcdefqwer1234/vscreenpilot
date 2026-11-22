using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Services;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>把 <see cref="SessionManager"/> 收窄为 IPC/CLI 需要的两个边界接口。</summary>
internal sealed class SessionFacade : ISessionFacade, IControlService
{
    private readonly SessionManager _session;

    public SessionFacade(SessionManager session)
    {
        _session = session;
    }

    public string ProfileName => _session.ProfileName;

    public bool Initialized => _session.Initialized;

    public string ArtifactDirectory => _session.ArtifactDirectory;

    public SnapshotResult Snapshot(SnapshotRequest request) =>
        _session.SnapshotOps.Snapshot(request.Pid, request.MaxDepth, request.InteractiveOnly,
            request.MaxNodes, request.IncludeValues, request.IncludeScreenshot,
            request.Scale, request.Annotate);

    public CaptureResult CaptureScreen(CaptureRequest request) =>
        _session.CaptureOps.CaptureScreenEx(request.X, request.Y, request.Width, request.Height,
            request.Scale, request.SavePath, request.StoreFrame, request.PreviewScale);

    public EnvironmentView GetEnvironment(bool ensure = false) =>
        _session.LifecycleOps.GetEnvironment(ensure);

    public IReadOnlyList<TrackedApp> GetApps() => _session.GetAppsSnapshot();

    public MemoryView TrimIdleMemory(string reason) => _session.LifecycleOps.TrimIdleMemory(reason);

    public IDisposable UseClient(ClientInfo client) =>
        _session.Clients.Use(new ClientContext
        {
            Id = client.Id,
            Name = client.Name,
            Cwd = client.Cwd ?? Environment.CurrentDirectory,
            Roots = client.Roots,
            IncludeAllApps = client.IncludeAllApps,
        });

    public int ActiveClientCount => _session.Clients.ActiveCount;

    public int OwnedAppCount => _session.Apps.Count;

    public JsonArray ClientSummaries(bool includeCwd = false) =>
        _session.ClientOps.ClientSummariesJson(includeCwd);

    public JsonObject LeaseDescribe() => _session.Lease.Describe();

    public void DropClientRefs(string clientId) => _session.ClientOps.DropClientRefs(clientId);

    public EnvironmentView EnsureEnvironment() => _session.LifecycleOps.GetEnvironment(ensure: true);

    public DisplayInfo? VirtualDisplay => _session.VirtualDisplay;

    public void Dispose() => _session.Dispose();
}
