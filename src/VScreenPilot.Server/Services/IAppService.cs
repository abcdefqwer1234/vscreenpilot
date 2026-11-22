using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>应用启动/归位。</summary>
internal interface IAppService
{
    LaunchOrPlaceView LaunchOrPlace(AppLaunchRequest request);
}

internal sealed class AppToolService : IAppService
{
    private readonly SessionManager _session;

    public AppToolService(SessionManager session) => _session = session;

    public LaunchOrPlaceView LaunchOrPlace(AppLaunchRequest request) =>
        _session.AppOps.LaunchOrPlaceApp(
            request.Path,
            request.Window,
            request.Args,
            request.WaitMs,
            request.NewInstance,
            request.EnsureDocument,
            request.Pid);
}
