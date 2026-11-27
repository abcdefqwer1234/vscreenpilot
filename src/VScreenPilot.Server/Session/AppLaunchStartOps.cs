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
        s.LaunchedPid = processId;

        try
        {
            WaitForLaunchedWindow(s, process, before);

            if (s.Window is null)
            {
                s.Alive = !process.HasExited;

                if (process.HasExited)
                {
                    s.ExitCode = process.ExitCode;
                }
            }
        }
        finally
        {
            process.Dispose();
        }

        if (s.Window is null)
        {
            // 单实例应用（如 Win11 记事本）无法创建新窗口时，先尝试接管已有的那个。
            // 控制台程序没有可接管的窗口，跳过这一步，避免白等 3 秒。
            if (s.HadRunningInstance && !AppProcessOps.IsConsoleImage(s.LaunchPath))
            {
                TryAdoptExistingWindow(s);
            }

            if (s.Window is null)
            {
                // 没有窗口不是失败：控制台/启动器/静默安装器本来就没有主窗口。
                // 关键：不再 KillProcessTree —— 长任务（安装、构建）不能因为"没等到窗口"被杀。
                // 同时不提前加入 Job（否则 daemon 空闲退出会连坐），由 on_no_window 决定是否终止。
                s.Alive = AppTermination.IsProcessAlive(processId);
                s.CloseOnRelease = false;
                s.Dedicated = false;

                if (s.Alive && s.OnNoWindow == "kill")
                {
                    AppTermination.KillProcessTree(processId);
                    s.Alive = false;
                    s.Warnings.Add("进程已启动但没有可见窗口；按 on_no_window=kill 已终止。");
                }
                else
                {
                    s.Warnings.Add(
                        s.Alive
                            ? "进程已启动但没有可见窗口；已保留进程（未加入作业对象），不会随 daemon 退出被杀。后续可用 get_processes/wait_for(process) 观察。"
                            : $"进程在窗口出现前已退出 (exit={s.ExitCode?.ToString() ?? "unknown"})。");
                }

                if (!s.Alive && s.TempProfile is not null)
                {
                    AppTermination.TryDeleteDirectory(s.TempProfile);
                }

                return;
            }
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

        return AppProcessOps.Start(startInfo);
    }

    private void WaitForLaunchedWindow(LaunchState s, Process process, HashSet<IntPtr> before)
    {
        bool console = AppProcessOps.IsConsoleImage(s.LaunchPath);
        s.Window = _core.WindowOps.WaitForNewWindow(
            process, s.ExeName, before, s.WaitMs, out int contentPid, out string contentPath,
            console ? 1200 : 5000, console);
        s.ContentPid = contentPid;
        s.ContentPath = contentPath;
    }

    private void TryAdoptExistingWindow(LaunchState s)
    {
        // 单实例应用（如 Win11 记事本）无法创建新窗口：自动降级为接管现有窗口。
        WindowInfo? reuse;

        try
        {
            reuse = FindExistingWindow(s);
        }
        catch (ToolException exception) when (
            exception.Code is "E_WINDOW_TIMEOUT" or "E_APP_EXITED")
        {
            // 已有进程也没有主窗口（控制台/启动器）：不接管，交给 no-window 分支。
            return;
        }

        if (reuse is null)
        {
            return;
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
