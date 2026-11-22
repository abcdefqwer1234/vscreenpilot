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

internal sealed record LaunchSessionContext(
    LogRing Log,
    JobObject Job,
    ClientSessionRegistry Clients,
    object Gate,
    Action SaveState);

internal sealed record LaunchAppContext(
    TrackedAppRegistry Apps,
    UiaHost Uia,
    Func<DisplayInfo> EnsureDisplay,
    Func<string, string> ResolvePath,
    Action<string> EnsureAppAllowed,
    Func<TrackedApp, bool> IsOwned,
    Func<TrackedApp, bool> IsLive);

internal sealed record LaunchFocusContext(
    Action<IntPtr> RestoreForeground,
    Action<IntPtr> StartFocusGuard,
    Action PauseFocusGuard,
    Action ResumeFocusGuard,
    Func<TrackedApp, bool> EnsureForeground);

internal sealed class AppLauncher
{
    internal readonly LaunchSessionContext _session;
    internal readonly LaunchAppContext _apps;
    internal readonly LaunchFocusContext _focus;
    internal readonly LogRing _log;
    internal readonly object _gate;
    internal readonly AppLaunchOps LaunchOps;
    internal readonly AppLaunchFlowOps LaunchFlowOps;
    internal readonly AppProcessOps ProcessOps;
    internal readonly AppUwpOps UwpOps;
    internal readonly AppWindowOps WindowOps;
    internal readonly AppDocumentOps DocumentOps;

    internal static bool LooksLikeRestoredNotepadTitle(
        string exeName, bool started, string? arguments, string? title) =>
        AppProcessOps.LooksLikeRestoredNotepadTitle(exeName, started, arguments, title);

    internal static bool IsSameExecutable(System.Diagnostics.Process process, string fullPath) =>
        AppProcessOps.IsSameExecutable(process, fullPath);

    internal static void CleanupStaleProfiles() => AppProcessOps.CleanupStaleProfiles();

    internal static bool LooksLikeDocumentWindow(string title, string product) =>
        AppDocumentOps.LooksLikeDocumentWindow(title, product);
    internal bool _jobUnavailableLogged;

    public AppLauncher(LaunchSessionContext session, LaunchAppContext apps, LaunchFocusContext focus)
    {
        _session = session;
        _apps = apps;
        _focus = focus;
        _log = session.Log;
        _gate = session.Gate;

        LaunchOps = new AppLaunchOps(this);
        LaunchFlowOps = new AppLaunchFlowOps(this);
        ProcessOps = new AppProcessOps(this);
        UwpOps = new AppUwpOps(this);
        WindowOps = new AppWindowOps(this);
        DocumentOps = new AppDocumentOps(this);
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
    internal (int Pid, string Path) ResolveUwpContentProcess(IntPtr frameHwnd)
    {
        if (frameHwnd == IntPtr.Zero || !UwpOps.IsApplicationFrame(frameHwnd))
        {
            return (0, string.Empty);
        }

        UiaTreeResult tree = _apps.Uia.BuildTree(
            new UiTreeRequest(frameHwnd, string.Empty, MaxDepth: 4, InteractiveOnly: false,
                MaxNodes: 300, TimeBudgetMs: 1200),
            () => false);

        if (tree.Root is null)
        {
            return (0, string.Empty);
        }

        var processIds = new HashSet<int>();
        UwpOps.CollectProcessIds(tree.Root, processIds);
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
