using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Ui;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager : IEnvironmentHost
{
    private readonly EnvironmentReporter _environment;

    object IEnvironmentHost.Gate => _gate;

    VddDevice IEnvironmentHost.Vdd => _vdd;

    bool IEnvironmentHost.Initialized => _initialized;

    string IEnvironmentHost.ProfileName => ProfileName;

    long IEnvironmentHost.Epoch => Epoch;

    JobObject IEnvironmentHost.Job => _job;

    ClientSessionRegistry IEnvironmentHost.Clients => Clients;

    JsonArray IEnvironmentHost.ClientSummaries() => ClientSummariesJson();

    Func<JsonObject>? IEnvironmentHost.ScreenLease => ScreenLeaseProvider;

    TrackedAppRegistry IEnvironmentHost.Apps => _apps;

    bool IEnvironmentHost.IsOwned(TrackedApp app) => IsOwned(app);

    bool IEnvironmentHost.IsLive(TrackedApp app) => IsLiveTrackedApp(app);

    UiaHost IEnvironmentHost.Uia => Uia;

    bool IEnvironmentHost.SelfHealed => _selfHealed;

    int IEnvironmentHost.SelfHealCount => _selfHealCount;

    string? IEnvironmentHost.LastSelfHealReason => _lastSelfHealReason;

    bool IEnvironmentHost.AdoptedOrphan => _adoptedOrphan;

    int IEnvironmentHost.DisplayRecreateCount => _displayRecreateCount;

    int IEnvironmentHost.VddReopenCount => _vddReopenCount;

    bool IEnvironmentHost.DisplayPositionFallback => _displayPositionFallback;

    int IEnvironmentHost.DisplayIndex => _displayIndex;

    string IEnvironmentHost.DisplayDevice => _displayDevice;

    DisplayInfo IEnvironmentHost.Ensure() => EnsureEnvironment();

    void IEnvironmentHost.PruneApps() => PruneDeadApps();

    DisplayInfo? IEnvironmentHost.OwnDisplay() => OwnDisplay();
}
