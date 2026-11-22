using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>新实例启动：AppLaunchOps 的拆分子集（启动流程较重，独立成类型）。</summary>
internal sealed class AppLaunchStartOps
{
    private readonly AppLauncher _core;

    public AppLaunchStartOps(AppLauncher core) => _core = core;

    internal void StartNewInstance(LaunchState s)
    {
        s.HadRunningInstance = ProbeRunningInstance(s.LaunchPath);
        HashSet<IntPtr> before = SnapshotTopLevelWindows();

        Process process = StartProcess(s);
        int processId = process.Id;
        s.Started = true;

        try
        {
            AttachToJob(process, processId);
            WaitForLaunchedWindow(s, process, before);
        }
        finally
        {
            process.Dispose();
        }

        if (s.Window is null)
        {
            RecoverWithoutNewWindow(s, processId);
        }

        // 单实例回退到“接管现有窗口”时，上面已把 closeOnRelease 置 false；
        // 这里不能无条件覆盖，否则 release 会把用户原有窗口当成本会话启动的应用关闭/强杀。
        (s.CloseOnRelease, s.Dedicated) = AppLauncher.ResolveNewInstanceOwnership(
            s.AdoptedExisting,
            s.HadRunningInstance,
            s.ExeName.Equals("msedge", StringComparison.OrdinalIgnoreCase));
    }

    private bool ProbeRunningInstance(string launchPath)
    {
        using Process? probe = _core.ProcessOps.FindRunningProcess(launchPath);
        return probe is not null;
    }

    private static HashSet<IntPtr> SnapshotTopLevelWindows() =>
        new(WindowManager.ListTopLevel().Select(item => item.Hwnd));

    private Process StartProcess(LaunchState s)
    {
        string? workingDirectory = Path.GetDirectoryName(s.LaunchPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = s.LaunchPath,
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                ? _core._session.Clients.CurrentCwd
                : workingDirectory,
        };

        startInfo.Arguments = _core.ProcessOps.BuildNewInstanceArguments(
            s.ExeName, s.Arguments, out string? launchTempProfile);
        s.TempProfile = launchTempProfile;

        return Process.Start(startInfo)
            ?? throw new ToolException("E_APP_START", $"无法启动应用: {s.LaunchPath}");
    }

    private void AttachToJob(Process process, int processId)
    {
        if (!_core._session.Job.Assign(process))
        {
            _core._log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
        }
    }

    private void WaitForLaunchedWindow(LaunchState s, Process process, HashSet<IntPtr> before)
    {
        s.Window = _core.WindowOps.WaitForNewWindow(
            process, s.ExeName, before, s.WaitMs, out int contentPid, out string contentPath);
        s.ContentPid = contentPid;
        s.ContentPath = contentPath;
    }

    private void RecoverWithoutNewWindow(LaunchState s, int processId)
    {
        AppTermination.KillProcessTree(processId);

        if (s.TempProfile is not null)
        {
            AppTermination.TryDeleteDirectory(s.TempProfile);
        }

        if (s.HadRunningInstance)
        {
            AdoptExistingWindowOrThrow(s);
            return;
        }

        throw new ToolException("E_WINDOW_TIMEOUT", "等待新窗口超时");
    }

    private void AdoptExistingWindowOrThrow(LaunchState s)
    {
        // 单实例应用（如 Win11 记事本）无法创建新窗口：自动降级为接管现有窗口。
        WindowInfo? reuse = FindExistingWindow(s);

        if (reuse is null)
        {
            throw new ToolException(
                "E_NEW_INSTANCE_UNSUPPORTED",
                "无法为已运行的应用创建独立窗口；请设置 new_instance=false 以接管现有窗口",
                new JsonObject
                {
                    ["app"] = s.ExeName,
                    ["suggest_reuse"] = true,
                },
                retryable: true,
                hint: "改用 window 参数按标题接管，或设置 new_instance=false",
                stage: "launch");
        }

        // 不记录窗口标题：可能包含文档名/客户名。
        _core._log.Warn($"无法创建独立实例，已接管现有窗口 pid={reuse.Pid}");
        bool alreadyOwned = _core.WindowOps.IsOwnedWindowOnCurrentClient(reuse.Hwnd);
        s.Window = reuse;
        s.NewInstance = false;
        s.Started = false;

        if (alreadyOwned)
        {
            return;
        }

        // 用户原本就有的窗口：不能算本会话启动，release 只归位、不关闭。
        s.CloseOnRelease = false;
        s.Dedicated = false;
        s.AdoptedExisting = true;
        s.LaunchWarning = "无法创建独立实例，已接管现有窗口；release_environment 只归位、不关闭该窗口";
    }

    private WindowInfo? FindExistingWindow(LaunchState s)
    {
        using Process? running = _core.ProcessOps.FindRunningProcess(s.Path!);

        if (running is null)
        {
            return null;
        }

        return _core.WindowOps.WaitForMainWindow(running, 3000);
    }
}
