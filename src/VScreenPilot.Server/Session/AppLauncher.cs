using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

internal interface IAppLaunchHost
{
    LogRing Log { get; }
    JobObject Job { get; }
    TrackedAppRegistry Apps { get; }
    ClientSessionRegistry Clients { get; }
    UiaHost Uia { get; }
    object Gate { get; }
    DisplayInfo EnsureDisplay();
    string ResolvePath(string path);
    void EnsureAppAllowed(string path);
    bool IsOwned(TrackedApp app);
    bool IsLive(TrackedApp app);
    void RestoreForeground(IntPtr previous, bool logFailure = true);
    void StartFocusGuard(IntPtr preferredUserForeground = default);
    void PauseFocusGuard(int leaseMs = 0);
    void ResumeFocusGuard();
    bool EnsureForeground(TrackedApp app);
    void SaveState();
}

internal sealed partial class AppLauncher
{
    private readonly IAppLaunchHost _host;
    private readonly LogRing _log;
    private readonly object _gate;
    private bool _jobUnavailableLogged;

    public AppLauncher(IAppLaunchHost host)
    {
        _host = host;
        _log = host.Log;
        _gate = host.Gate;
    }

    /// <summary>
    /// 解析“本次启动”的窗口所有权。单实例回退（adoptedExisting=true）必须保留
    /// closeOnRelease=false，否则 release 会关闭用户原有窗口。
    /// </summary>
    internal static (bool CloseOnRelease, bool Dedicated) ResolveNewInstanceOwnership(
        bool adoptedExisting, bool hadRunningInstance, bool isEdge)
    {
        if (adoptedExisting)
        {
            return (false, false);
        }

        return (true, !hadRunningInstance || isEdge);
    }

    /// <summary>从 frame 的 UIA 树里找非 frame-host 的内容进程。</summary>
    private (int Pid, string Path) ResolveUwpContentProcess(IntPtr frameHwnd)
    {
        if (frameHwnd == IntPtr.Zero || !IsApplicationFrame(frameHwnd))
        {
            return (0, string.Empty);
        }

        UiaTreeResult tree = _host.Uia.BuildTree(
            frameHwnd,
            rootRuntimeId: string.Empty,
            maxDepth: 4,
            interactiveOnly: false,
            maxNodes: 300,
            timeBudgetMs: 1200,
            isCancelled: () => false);

        if (tree.Root is null)
        {
            return (0, string.Empty);
        }

        var processIds = new HashSet<int>();
        CollectProcessIds(tree.Root, processIds);
        int framePid = WindowManager.GetProcessId(frameHwnd);

        foreach (int pid in processIds)
        {
            if (pid == framePid)
            {
                continue;
            }

            string path = WindowManager.GetProcessPath(pid);

            if (!string.IsNullOrEmpty(path))
            {
                return (pid, path);
            }
        }

        return (0, string.Empty);
    }

}
