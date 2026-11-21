using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using ObserveSignature = VScreenPilot.Verification.ObserveSampler.Signature;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager : IInputHost
{
    LogRing IInputHost.Log => _log;

    ClientSessionRegistry IInputHost.Clients => Clients;

    UiaHost IInputHost.Uia => Uia;

    DisplayInfo IInputHost.EnsureDisplay() => EnsureEnvironmentFast();

    DisplayInfo IInputHost.Ensure() => EnsureEnvironment();

    CancellationToken IInputHost.Cancellation => Cancellation;

    ElementRefEntry IInputHost.RequireElement(string reference) => RequireElementEntry(reference);

    UiaElementHandle IInputHost.ResolveHandle(
        string reference, DisplayInfo display, out ElementRefEntry entry, out bool refreshed) =>
        ResolveElementHandle(reference, display, out entry, out refreshed);

    TrackedApp? IInputHost.FindTrackedAppAtWindow(uint targetPid, uint rootPid)
    {
        lock (_gate)
        {
            return _apps.FirstOrDefault(
                app => IsOwned(app) &&
                       (app.Pid == (int)targetPid || app.Pid == (int)rootPid) &&
                       IsLiveTrackedApp(app));
        }
    }

    TrackedApp IInputHost.RequirePointInTrackedApp(DisplayInfo display, int x, int y, string label) =>
        RequirePointInTrackedApp(display, x, y, label);

    void IInputHost.RequireWindowOnDisplay(DisplayInfo display, IntPtr hwnd, string label) =>
        RequireWindowOnDisplay(display, hwnd, label);

    void IInputHost.RequirePointInDisplay(DisplayInfo display, int x, int y, string label) =>
        RequirePointInDisplay(display, x, y, label);

    bool IInputHost.EnsureForeground(TrackedApp app) => EnsureForeground(app);

    bool IInputHost.IsAppForeground(TrackedApp app) => IsAppForeground(app);

    TrackedApp? IInputHost.LastTrackedApp() => LastLiveTrackedApp();

    TrackedApp? IInputHost.FindLiveTrackedApp(int? pid) => FindLiveTrackedApp(pid);

    void IInputHost.RestoreForeground(IntPtr previous, bool logFailure) =>
        RestoreForeground(previous, logFailure);

    bool IInputHost.IsInputDesktopAvailable() => IsInputDesktopAvailable();

    ObserveSignature IInputHost.CaptureSignature(
        DisplayInfo display, int x, int y, int width, int height) =>
        CaptureSignature(display, x, y, width, height);

    JsonObject? IInputHost.BuildObserved(
        ObserveSignature? before, ObserveSignature? after, double milliseconds) =>
        BuildObserved(before, after, milliseconds);

    void IInputHost.PauseFocusGuard(int leaseMs) => PauseFocusGuard(leaseMs);

    void IInputHost.ResumeFocusGuard() => ResumeFocusGuard();

    TextWriteResult IInputHost.WriteTextVerified(
        TrackedApp app, UiaElementHandle? target, string text, bool replace, int timeoutMs) =>
        _input.WriteTextVerified(app, target, text, replace, timeoutMs);

    void IInputHost.PauseCursor() => _cursorGuard?.Pause();

    void IInputHost.ResumeCursor() => _cursorGuard?.Resume();

    void IInputHost.ResetUia() => ResetUia();
}
