using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>AppOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionAppOps
{
    internal readonly SessionManager _host;

    public SessionAppOps(SessionManager host) => _host = host;
    public LaunchOrPlaceView LaunchOrPlaceApp(
        string? path,
        string? windowTitle,
        string? arguments,
        int waitMs,
        bool newInstance,
        bool ensureDocument = true,
        int? pid = null) =>
        _host._launcher.Launch(path, windowTitle, arguments, waitMs, newInstance, ensureDocument, pid);
}
