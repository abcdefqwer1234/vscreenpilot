using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager : IAppLaunchHost
{
    LogRing IAppLaunchHost.Log => _log;

    JobObject IAppLaunchHost.Job => _job;

    TrackedAppRegistry IAppLaunchHost.Apps => _apps;

    ClientSessionRegistry IAppLaunchHost.Clients => Clients;

    UiaHost IAppLaunchHost.Uia => Uia;

    object IAppLaunchHost.Gate => _gate;

    DisplayInfo IAppLaunchHost.EnsureDisplay() => EnsureEnvironment();

    string IAppLaunchHost.ResolvePath(string path) => _paths.Resolve(path);

    void IAppLaunchHost.EnsureAppAllowed(string path) => EnsureAppAllowed(path);

    bool IAppLaunchHost.IsOwned(TrackedApp app) => IsOwned(app);

    bool IAppLaunchHost.IsLive(TrackedApp app) => TrackedAppTracker.IsLive(app);

    void IAppLaunchHost.RestoreForeground(IntPtr previous, bool logFailure) =>
        RestoreForeground(previous, logFailure);

    void IAppLaunchHost.StartFocusGuard(IntPtr preferredUserForeground) =>
        StartFocusGuard(preferredUserForeground);

    void IAppLaunchHost.PauseFocusGuard(int leaseMs) => PauseFocusGuard(leaseMs);

    void IAppLaunchHost.ResumeFocusGuard() => ResumeFocusGuard();

    bool IAppLaunchHost.EnsureForeground(TrackedApp app) => EnsureForeground(app);

    void IAppLaunchHost.SaveState() => SaveState();
}
