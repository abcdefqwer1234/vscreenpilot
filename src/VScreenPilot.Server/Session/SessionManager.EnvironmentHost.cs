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

    VddDevice IEnvironmentHost.Vdd => _display.Vdd;

    bool IEnvironmentHost.Initialized => _display.State.Initialized;

    string IEnvironmentHost.ProfileName => ProfileName;

    long IEnvironmentHost.Epoch => Epoch;

    JobObject IEnvironmentHost.Job => _job;

    ClientSessionRegistry IEnvironmentHost.Clients => Clients;

    JsonArray IEnvironmentHost.ClientSummaries() => ClientSummariesJson();

    Func<JsonObject>? IEnvironmentHost.ScreenLease => ScreenLeaseProvider;

    TrackedAppRegistry IEnvironmentHost.Apps => _apps;

    bool IEnvironmentHost.IsOwned(TrackedApp app) => IsOwned(app);

    bool IEnvironmentHost.IsLive(TrackedApp app) => TrackedAppTracker.IsLive(app);

    UiaHost IEnvironmentHost.Uia => Uia;

    bool IEnvironmentHost.SelfHealed => _display.State.SelfHealed;

    int IEnvironmentHost.SelfHealCount => _display.State.SelfHealCount;

    string? IEnvironmentHost.LastSelfHealReason => _display.State.LastSelfHealReason;

    bool IEnvironmentHost.AdoptedOrphan => _display.State.AdoptedOrphan;

    int IEnvironmentHost.DisplayRecreateCount => _display.State.RecreateCount;

    int IEnvironmentHost.VddReopenCount => _display.State.VddReopenCount;

    bool IEnvironmentHost.DisplayPositionFallback => _display.State.PositionFallback;

    int IEnvironmentHost.DisplayIndex => _display.State.Index;

    string IEnvironmentHost.DisplayDevice => _display.State.DeviceName;

    DisplayInfo IEnvironmentHost.Ensure() => EnsureEnvironment();

    void IEnvironmentHost.PruneApps() => PruneDeadApps();

    DisplayInfo? IEnvironmentHost.OwnDisplay() => OwnDisplay();
}
