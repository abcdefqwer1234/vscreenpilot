using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>AppOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionAppOps
{
    internal readonly SessionManager _host;

    public SessionAppOps(SessionManager host) => _host = host;
    public LaunchOrPlaceView LaunchOrPlaceApp(LaunchRequest request) =>
        _host._launcher.LaunchOps.Launch(request);
}
