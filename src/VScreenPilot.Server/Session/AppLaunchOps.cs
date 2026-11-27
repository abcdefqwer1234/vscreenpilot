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

/// <summary>launch_or_place_app 的目标。</summary>
internal sealed record LaunchRequest(
    string? Path, string? WindowTitle, string? Arguments, int? Pid, LaunchOptions Options)
{
    /// <summary>按 hwnd 精确接管（对话框/独立弹窗）；优先于 path/window/pid。</summary>
    public long? Hwnd { get; init; }
}

/// <summary>launch_or_place_app 的行为选项。</summary>
internal sealed record LaunchOptions(
    int WaitMs, bool NewInstance, bool EnsureDocument, bool Place = true, bool Maximize = true,
    string OnNoWindow = "leave", string Lifecycle = "session", string Fit = "clamp");

/// <summary>LaunchOps：AppLauncher 的拆分子集。</summary>
internal sealed class AppLaunchOps
{
    private readonly AppLauncher _core;

    internal readonly AppLaunchStartOps StartOps;

    public AppLaunchOps(AppLauncher core)
    {
        _core = core;
        StartOps = new AppLaunchStartOps(core);
    }

    public LaunchOrPlaceView Launch(LaunchRequest request)
    {
        string? path = request.Path;
        string? windowTitle = request.WindowTitle;
        string? arguments = request.Arguments;
        int waitMs = request.Options.WaitMs;
        bool newInstance = request.Options.NewInstance;
        bool ensureDocument = request.Options.EnsureDocument;
        bool place = request.Options.Place;
        bool maximize = request.Options.Maximize;
        string onNoWindow = (request.Options.OnNoWindow ?? "leave").Trim().ToLowerInvariant();
        string lifecycle = (request.Options.Lifecycle ?? "session").Trim().ToLowerInvariant();
        string fit = (request.Options.Fit ?? "clamp").Trim().ToLowerInvariant();
        int? pid = request.Pid;

        if (onNoWindow is not ("leave" or "kill"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"on_no_window 只能是 leave/kill（收到 {request.Options.OnNoWindow}）");
        }

        if (fit is not ("clamp" or "keep"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"fit 只能是 clamp/keep（收到 {request.Options.Fit}）",
                retryable: false,
                hint: "clamp 会把超出虚拟屏的窗口缩到屏幕内；keep 保持原尺寸",
                stage: "validate");
        }

        if (lifecycle is not ("session" or "detached"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"lifecycle 只能是 session/detached（收到 {request.Options.Lifecycle}）",
                retryable: false,
                hint: "长任务需要 daemon 崩溃后应用继续运行，使用 lifecycle=detached",
                stage: "validate");
        }

        bool detached = lifecycle == "detached";

        long? hwnd = request.Hwnd;
        var s = new LaunchState
        {
            Path = path,
            WindowTitle = windowTitle,
            Arguments = arguments,
            WaitMs = waitMs,
            NewInstance = newInstance,
            EnsureDocument = ensureDocument,
            Place = place,
            Maximize = maximize,
            OnNoWindow = onNoWindow,
            Detached = detached,
            Fit = fit,
            Pid = pid,
            TargetHwnd = hwnd,
            UserForeground = WindowManager.GetForegroundWindow(),
        };

        ResolveTarget(s);

        // 无窗口是合法结果：控制台/启动器/静默安装器本来就没有主窗口。
        // 以前这里会杀进程树并抛 E_WINDOW_TIMEOUT，既误判又破坏长任务。
        if (s.Window is null)
        {
            return BuildNoWindowView(s);
        }

        // 只有确实拿到窗口才挂载/校屏，避免为了跑一条控制台命令把虚拟屏挂起来。
        s.Display = _core._apps.EnsureDisplay();
        _core.LaunchFlowOps.TrackWindow(s);
        PresentWindow(s);
        _core.LaunchFlowOps.EnsureDocumentReady(s);
        return BuildView(s);
    }

    internal void ResolveTarget(LaunchState s)
    {
        if (s.TargetHwnd is { } target && target > 0)
        {
            ResolveByHwnd(s);
            return;
        }

        if (!string.IsNullOrWhiteSpace(s.WindowTitle))
        {
            ResolveByTitle(s);
            return;
        }

        if (s.Pid is not null)
        {
            ResolveByPid(s);
            return;
        }

        if (!string.IsNullOrWhiteSpace(s.Path))
        {
            ResolveByPath(s);
            return;
        }

        throw new ToolException(
            "E_INVALID_ARG", "path、window、pid 或 hwnd 至少提供一个");
    }

    internal void ResolveByHwnd(LaunchState s)
    {
        IntPtr hwnd = new(s.TargetHwnd!.Value);

        if (!NativeMethods.IsWindow(hwnd))
        {
            throw new ToolException(
                "E_WINDOW_NOT_FOUND",
                $"hwnd=0x{s.TargetHwnd.Value:X} 不是有效窗口",
                new JsonObject { ["hwnd"] = s.TargetHwnd.Value });
        }

        if (NativeMethods.GetAncestor(hwnd, NativeMethods.GaRoot) != hwnd)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"hwnd=0x{s.TargetHwnd.Value:X} 是子控件，不是顶层窗口",
                new JsonObject { ["hwnd"] = s.TargetHwnd.Value },
                retryable: false,
                hint: "用 list_windows 取顶层窗口；子控件请用 click_ref/find_element",
                stage: "window");
        }

        int ownerPid = WindowManager.GetProcessId(hwnd);
        s.Window = WindowManager.FromHandle(hwnd)
            ?? throw new ToolException("E_WINDOW_NOT_FOUND", "窗口已失效");
        s.Pid = ownerPid;
        s.NewInstance = false;
        s.ExeName = Path.GetFileNameWithoutExtension(
            WindowManager.GetProcessPath(ownerPid) ?? string.Empty);
    }

    internal void ResolveByTitle(LaunchState s)
    {
        IReadOnlyList<WindowInfo> candidates = WindowManager.FindByTitleCandidates(s.WindowTitle!);

        if (candidates.Count == 0)
        {
            throw new ToolException("E_WINDOW_NOT_FOUND", $"找不到标题匹配的窗口: {s.WindowTitle}");
        }

        if (candidates.Count > 1)
        {
            // 只有唯一完全相等时才继续；否则把候选返回给模型，避免误接管用户窗口。
            List<WindowInfo> exact = candidates
                .Where(item => item.Title.Equals(s.WindowTitle, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exact.Count != 1)
            {
                var list = new JsonArray();

                foreach (WindowInfo candidate in candidates.Take(10))
                {
                    list.Add((JsonNode)new JsonObject
                    {
                        ["hwnd"] = candidate.Hwnd.ToInt64(),
                        ["pid"] = candidate.Pid,
                        ["title"] = candidate.Title,
                    });
                }

                throw new ToolException(
                    "E_WINDOW_AMBIGUOUS",
                    $"标题匹配到 {candidates.Count} 个窗口，拒绝猜一个接管；请用 pid 精确指定",
                    new JsonObject
                    {
                        ["title"] = s.WindowTitle,
                        ["windows"] = list.DeepClone(),
                        ["candidates"] = list,
                    },
                    retryable: true,
                    hint: "传更完整的标题，或改用 pid 精确指定（details.windows 给出候选）",
                    stage: "window");
            }

            candidates = exact;
        }

        s.Window = candidates[0];

        // 显式指定窗口时按接管处理，不创建新实例。
        s.NewInstance = false;
    }

    internal void ResolveByPid(LaunchState s)
    {
        // 按 pid 接管是最精确的方式：UWP/单实例/多标签都绕开了标题歧义。
        // 普通应用有主窗口；UWP 的内容进程没有顶层窗口，只能反查 frame host。
        s.Window = WindowManager.FindMainWindow(s.Pid!.Value)
            ?? _core.UwpOps.FindUwpFrameForProcess(s.Pid!.Value)
            ?? throw new ToolException(
                "E_WINDOW_NOT_FOUND",
                $"pid={s.Pid} 没有可见主窗口",
                new JsonObject { ["pid"] = s.Pid });

        s.NewInstance = false;
        s.ExeName = _core.UwpOps.GetProcessName(s.Pid!.Value);

        if (s.Window!.Pid != s.Pid!.Value)
        {
            s.ContentPid = s.Pid!.Value;
            s.ContentPath = WindowManager.GetProcessPath(s.Pid!.Value);
        }
    }

    internal void ResolveByPath(LaunchState s)
    {
        bool hasDirectory = Path.IsPathRooted(s.Path!) ||
                            s.Path!.Contains('\\') ||
                            s.Path!.Contains('/');
        s.LaunchPath = hasDirectory ? _core._apps.ResolvePath(s.Path) : s.Path;
        _core._apps.EnsureAppAllowed(s.LaunchPath);

        if (File.Exists(s.LaunchPath) == false && hasDirectory)
        {
            throw new ToolException("E_APP_START", $"应用不存在: {s.LaunchPath}");
        }

        s.ExeName = Path.GetFileNameWithoutExtension(s.LaunchPath);
        if (s.NewInstance)
        {
            StartNewInstance(s);
        }
        else
        {
            LaunchOrReuse(s);
        }
    }

    internal void StartNewInstance(LaunchState s) => StartOps.StartNewInstance(s);

    internal void LaunchOrReuse(LaunchState s)
    {
        Process? running = _core.ProcessOps.FindRunningProcess(s.LaunchPath);
        s.HadRunningInstance = running is not null;

        if (running is not null)
        {
            s.LaunchedPid = running.Id;

            using (running)
            {
                try
                {
                    s.Window = _core.WindowOps.WaitForMainWindow(running, s.WaitMs);
                }
                catch (ToolException exception) when (
                    exception.Code is "E_WINDOW_TIMEOUT" or "E_APP_EXITED")
                {
                    // 已运行但没有主窗口（控制台/启动器）：如实返回，不杀进程。
                    s.Alive = !running.HasExited;
                    s.ExitCode = s.Alive ? null : TryExitCode(running);
                    s.CloseOnRelease = false;
                    s.Dedicated = false;
                    s.Warnings.Add(s.OnNoWindow == "kill"
                        ? "进程已在运行且没有可见主窗口；为免误杀既有进程，未执行 on_no_window=kill。"
                        : "进程已在运行但没有可见主窗口；已保留进程。");
                    return;
                }
            }

            s.Dedicated = false;
        }
        else
        {
            var before = new HashSet<IntPtr>(
                WindowManager.ListTopLevel().Select(item => item.Hwnd));

            string? workingDirectory = Path.GetDirectoryName(s.LaunchPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = s.LaunchPath,
                UseShellExecute = false,
                WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                    ? _core._session.Clients.CurrentCwd
                    : workingDirectory,
            };

            if (!string.IsNullOrWhiteSpace(s.Arguments))
            {
                startInfo.Arguments = s.Arguments;
            }

            Process process = AppProcessOps.Start(startInfo);

            int processId = process.Id;
            s.Started = true;
            s.LaunchedPid = processId;

            using (process)
            {
                int launchedContentPid;
                string launchedContentPath;
                bool console = AppProcessOps.IsConsoleImage(s.LaunchPath);
                s.Window = _core.WindowOps.WaitForNewWindow(
                    process, s.ExeName, before, s.WaitMs, out launchedContentPid, out launchedContentPath,
                    console ? 1200 : 5000, console);
                s.ContentPid = launchedContentPid;
                s.ContentPath = launchedContentPath;

                if (s.Window is null && process.HasExited)
                {
                    s.ExitCode = process.ExitCode;
                }
            }

            if (s.Window is null)
            {
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
                            ? "进程已启动但没有可见窗口；已保留进程（未加入作业对象），不会随 daemon 退出被杀。"
                            : $"进程在窗口出现前已退出 (exit={s.ExitCode?.ToString() ?? "unknown"})。");
                }

                return;
            }

            s.CloseOnRelease = true;
            s.Dedicated = true;
        }
    }


    internal void PresentWindow(LaunchState s)
    {
        s.PreviousForeground = s.UserForeground;

        if (s.Place)
        {
            if (s.Maximize)
            {
                WindowManager.MoveAndMaximize(s.Window!.Hwnd, s.Display.X, s.Display.Y, s.Display.Width, s.Display.Height);
            }
            else
            {
                WindowManager.MoveResize(s.Window!.Hwnd, s.Display.X, s.Display.Y, s.Display.Width, s.Display.Height);
            }
        }

        // 应用启动/会话恢复可能在稍后多次激活自己；同步归还一次，再持续观察一小段时间。
        Thread.Sleep(500);
        _core._focus.RestoreForeground(s.PreviousForeground);
        _core._focus.StartFocusGuard(s.PreviousForeground);
        s.Maximized = s.Place && s.Maximize &&
                      WindowManager.WaitForZoomed(s.Window!.Hwnd, TimeSpan.FromSeconds(2));
        Thread.Sleep(300);
        s.After = WindowManager.FromHandle(s.Window!.Hwnd);
        (int Left, int Top, int Width, int Height) visibleBefore =
            WindowManager.GetVisibleBounds(s.Window.Hwnd);
        s.RequestedBounds = ToOptionalBounds(visibleBefore);
        s.AppliedBounds = s.RequestedBounds;

        if (s.Place && s.Fit == "clamp" &&
            (visibleBefore.Width > s.Display.Width || visibleBefore.Height > s.Display.Height))
        {
            int width = Math.Min(visibleBefore.Width, s.Display.Width);
            int height = Math.Min(visibleBefore.Height, s.Display.Height);

            WindowManager.MoveResize(s.Window.Hwnd, s.Display.X, s.Display.Y, width, height);
            s.Maximized = false;
            s.After = WindowManager.FromHandle(s.Window.Hwnd);
            (int Left, int Top, int Width, int Height) visibleAfter =
                WindowManager.GetVisibleBounds(s.Window.Hwnd);
            s.AppliedBounds = ToOptionalBounds(visibleAfter);
            s.FitAdjusted = visibleAfter.Width != visibleBefore.Width ||
                            visibleAfter.Height != visibleBefore.Height;

            if (s.Tracked is not null)
            {
                // clamp 后的尺寸必须写回跟踪信息，否则 release/崩溃恢复会还原成超屏尺寸。
                s.Tracked.Placement = WindowManager.GetPlacement(s.Window.Hwnd);
                s.Tracked.PlacementSaved = true;
            }

            if (visibleAfter.Width > s.Display.Width || visibleAfter.Height > s.Display.Height)
            {
                s.Warnings.Add(
                    $"窗口 {visibleAfter.Width}×{visibleAfter.Height} 仍超过虚拟屏（可能受最小尺寸限制）；读取时使用 on_out_of_bounds=clip");
            }
            else
            {
                s.Warnings.Add($"窗口已缩到虚拟屏 {width}×{height} 内");
            }
        }

        _core._session.SaveState();

        if (!s.Place)
        {
            s.Warnings.Add("place=false：窗口未移到虚拟屏；若它不在虚拟屏内，输入工具会拒绝操作");
        }


        if (s.LaunchWarning is not null)
        {
            s.Warnings.Add(s.LaunchWarning);
        }
        else if (AppProcessOps.LooksLikeRestoredNotepadTitle(
                     s.ExeName, s.Started, s.Arguments, s.After?.Title ?? s.Window!.Title))
        {
            s.Warnings.Add(
                "记事本可能恢复了上次会话的标签；请先用 snapshot 确认标题，或传 args 指定文件");
        }
    }


    private static int? TryExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static OptionalBoundsView ToOptionalBounds(
        (int Left, int Top, int Width, int Height) bounds) =>
        bounds.Width <= 0 || bounds.Height <= 0
            ? new OptionalBoundsView(null, null, null, null)
            : new OptionalBoundsView(bounds.Left, bounds.Top, bounds.Width, bounds.Height);

    internal LaunchOrPlaceView BuildNoWindowView(LaunchState s)
    {
        int pid = s.Pid ?? s.LaunchedPid;

        return new LaunchOrPlaceView(
            s.Started,
            s.NewInstance,
            !s.Started,
            Maximized: false,
            CloseOnRelease: false,
            Hwnd: 0,
            Pid: pid,
            Title: string.Empty,
            DocumentReady: null,
            DocumentReadyReason: "no_window",
            EnsureDocument: s.EnsureDocument,
            Bounds: new OptionalBoundsView(null, null, null, null),
            Warnings: s.Warnings.Count > 0 ? s.Warnings : null,
            Profile: s.TempProfile,
            Hint: "目标进程没有可见窗口；已保留进程。用 get_processes 查看，或 wait_for(process) 等它退出。",
            Dialogs: [])
        {
            Window = false,
            Alive = s.Alive,
            ExitCode = s.ExitCode,
            Detached = s.Detached,
        };
    }

    internal LaunchOrPlaceView BuildView(LaunchState s)
    {
        var dialogs = new List<LaunchDialogView>();

        foreach (WindowInfo dialog in WindowManager.ListOwnedWindows(s.Window!.Pid, s.Window!.Hwnd))
        {
            dialogs.Add(new LaunchDialogView(dialog.Hwnd.ToInt64(), dialog.Title, dialog.ClassName));
        }

        return new LaunchOrPlaceView(
            s.Started,
            s.NewInstance,
            !s.Started,
            s.Maximized,
            s.Tracked?.CloseWindowOnRelease ?? s.CloseOnRelease,
            s.Window!.Hwnd.ToInt64(),
            s.Window!.Pid,
            s.After?.Title ?? s.Window!.Title,
            s.DocumentReady,
            s.DocumentReason,
            s.EnsureDocument,
            s.After is null
                ? new OptionalBoundsView(null, null, null, null)
                : new OptionalBoundsView(s.After.Rect.Left, s.After.Rect.Top, s.After.Width, s.After.Height),
            s.Warnings.Count > 0 ? s.Warnings : null,
            s.TempProfile,
            !s.NewInstance && s.HadRunningInstance
                ? s.Tracked?.CloseWindowOnRelease == true
                    ? "已复用本会话已启动的窗口；release_environment 会关闭它"
                    : "已接管现有窗口；关闭调试后只归位、不关闭该应用"
                : null,
            dialogs)
        {
            Detached = s.Tracked?.Detached ?? s.Detached,
            FitAdjusted = s.FitAdjusted,
            RequestedBounds = s.RequestedBounds,
            AppliedBounds = s.AppliedBounds,
        };
    }
}
