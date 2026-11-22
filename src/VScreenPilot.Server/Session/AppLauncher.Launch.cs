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

internal sealed partial class AppLauncher
{

    public LaunchOrPlaceView Launch(
        string? path,
        string? windowTitle,
        string? arguments,
        int waitMs,
        bool newInstance,
        bool ensureDocument = true,
        int? pid = null)
    {
        var s = new LaunchState
        {
            Path = path,
            WindowTitle = windowTitle,
            Arguments = arguments,
            WaitMs = waitMs,
            NewInstance = newInstance,
            EnsureDocument = ensureDocument,
            Pid = pid,
            Display = _apps.EnsureDisplay(),
            UserForeground = WindowManager.GetForegroundWindow(),
        };

        ResolveTarget(s);
        TrackWindow(s);
        PresentWindow(s);
        EnsureDocumentReady(s);
        return BuildView(s);
    }

    private void ResolveTarget(LaunchState s)
    {
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

        throw new ToolException("E_INVALID_ARG", "path 和 window 至少提供一个");
    }

    private void ResolveByTitle(LaunchState s)
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

    private void ResolveByPid(LaunchState s)
    {
        // 按 pid 接管是最精确的方式：UWP/单实例/多标签都绕开了标题歧义。
        // 普通应用有主窗口；UWP 的内容进程没有顶层窗口，只能反查 frame host。
        s.Window = WindowManager.FindMainWindow(s.Pid!.Value)
            ?? FindUwpFrameForProcess(s.Pid!.Value)
            ?? throw new ToolException(
                "E_WINDOW_NOT_FOUND",
                $"pid={s.Pid} 没有可见主窗口",
                new JsonObject { ["pid"] = s.Pid });

        s.NewInstance = false;
        s.ExeName = GetProcessName(s.Pid!.Value);

        if (s.Window!.Pid != s.Pid!.Value)
        {
            s.ContentPid = s.Pid!.Value;
            s.ContentPath = WindowManager.GetProcessPath(s.Pid!.Value);
        }
    }

    private void ResolveByPath(LaunchState s)
    {
        bool hasDirectory = Path.IsPathRooted(s.Path!) ||
                            s.Path!.Contains('\\') ||
                            s.Path!.Contains('/');
        s.LaunchPath = hasDirectory ? _apps.ResolvePath(s.Path) : s.Path;
        _apps.EnsureAppAllowed(s.LaunchPath);

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

    private void StartNewInstance(LaunchState s)
    {
        using (Process? probe = FindRunningProcess(s.LaunchPath))
        {
            s.HadRunningInstance = probe is not null;
        }

        var before = new HashSet<IntPtr>(
            WindowManager.ListTopLevel().Select(item => item.Hwnd));

        string? workingDirectory = Path.GetDirectoryName(s.LaunchPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = s.LaunchPath,
            UseShellExecute = false,
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                ? _session.Clients.CurrentCwd
                : workingDirectory,
        };

        string? launchTempProfile;
        startInfo.Arguments = BuildNewInstanceArguments(s.ExeName, s.Arguments, out launchTempProfile);
        s.TempProfile = launchTempProfile;

        Process process = Process.Start(startInfo)
            ?? throw new ToolException("E_APP_START", $"无法启动应用: {s.LaunchPath}");

        int processId = process.Id;
        s.Started = true;

        using (process)
        {
            if (!_session.Job.Assign(process))
            {
                _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
            }

            int launchedContentPid;
            string launchedContentPath;
            s.Window = WaitForNewWindow(
                process, s.ExeName, before, s.WaitMs, out launchedContentPid, out launchedContentPath);
            s.ContentPid = launchedContentPid;
            s.ContentPath = launchedContentPath;
        }

        if (s.Window is null)
        {
            AppReleaser.KillProcessTree(processId);
            if (s.TempProfile is not null)
            {
                AppReleaser.TryDeleteDirectory(s.TempProfile);
            }

            if (s.HadRunningInstance)
            {
                // 单实例应用（如 Win11 记事本）无法创建新窗口：自动降级为接管现有窗口。
                WindowInfo? reuse = null;

                using (Process? running = FindRunningProcess(s.Path!))
                {
                    if (running is not null)
                    {
                        reuse = WaitForMainWindow(running, 3000);
                    }
                }

                if (reuse is not null)
                {
                    // 不记录窗口标题：可能包含文档名/客户名。
                    _log.Warn($"无法创建独立实例，已接管现有窗口 pid={reuse.Pid}");
                    bool alreadyOwned = IsOwnedWindowOnCurrentClient(reuse.Hwnd);
                    s.Window = reuse;
                    s.NewInstance = false;
                    s.Started = false;

                    if (!alreadyOwned)
                    {
                        // 用户原本就有的窗口：不能算本会话启动，release 只归位、不关闭。
                        s.CloseOnRelease = false;
                        s.Dedicated = false;
                        s.AdoptedExisting = true;
                        s.LaunchWarning =
                            "无法创建独立实例，已接管现有窗口；release_environment 只归位、不关闭该窗口";
                    }
                }
                else
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
            }
            else
            {
                throw new ToolException("E_WINDOW_TIMEOUT", "等待新窗口超时");
            }
        }

        // 单实例回退到“接管现有窗口”时，上面已把 closeOnRelease 置 false；
        // 这里不能无条件覆盖，否则 release 会把用户原有窗口当成本会话启动的应用关闭/强杀。
        (s.CloseOnRelease, s.Dedicated) = ResolveNewInstanceOwnership(
            s.AdoptedExisting,
            s.HadRunningInstance,
            s.ExeName.Equals("msedge", StringComparison.OrdinalIgnoreCase));
    }

    private void LaunchOrReuse(LaunchState s)
    {
        Process? running = FindRunningProcess(s.LaunchPath);
        s.HadRunningInstance = running is not null;

        if (running is not null)
        {
            using (running)
            {
                s.Window = WaitForMainWindow(running, s.WaitMs);
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
                    ? _session.Clients.CurrentCwd
                    : workingDirectory,
            };

            if (!string.IsNullOrWhiteSpace(s.Arguments))
            {
                startInfo.Arguments = s.Arguments;
            }

            Process process = Process.Start(startInfo)
                ?? throw new ToolException("E_APP_START", $"无法启动应用: {s.LaunchPath}");

            int processId = process.Id;
            s.Started = true;

            using (process)
            {
                if (!_session.Job.Assign(process))
                {
                    _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                }

                int launchedContentPid;
                string launchedContentPath;
                s.Window = WaitForNewWindow(
                    process, s.ExeName, before, s.WaitMs, out launchedContentPid, out launchedContentPath);
                s.ContentPid = launchedContentPid;
                s.ContentPath = launchedContentPath;
            }

            if (s.Window is null)
            {
                AppReleaser.KillProcessTree(processId);
                throw new ToolException("E_WINDOW_TIMEOUT", "等待新窗口超时");
            }

            s.CloseOnRelease = true;
            s.Dedicated = true;
        }
    }


    private void PresentWindow(LaunchState s)
    {
        s.PreviousForeground = s.UserForeground;
        WindowManager.MoveAndMaximize(s.Window!.Hwnd, s.Display.X, s.Display.Y, s.Display.Width, s.Display.Height);
        // 应用启动/会话恢复可能在稍后多次激活自己；同步归还一次，再持续观察一小段时间。
        Thread.Sleep(500);
        _focus.RestoreForeground(s.PreviousForeground);
        _focus.StartFocusGuard(s.PreviousForeground);
        s.Maximized = WindowManager.WaitForZoomed(s.Window!.Hwnd, TimeSpan.FromSeconds(2));
        Thread.Sleep(300);
        s.After = WindowManager.FromHandle(s.Window!.Hwnd);
        _session.SaveState();


        if (s.LaunchWarning is not null)
        {
            s.Warnings.Add(s.LaunchWarning);
        }
        else if (LooksLikeRestoredNotepadTitle(
                     s.ExeName, s.Started, s.Arguments, s.After?.Title ?? s.Window!.Title))
        {
            s.Warnings.Add(
                "记事本可能恢复了上次会话的标签；请先用 snapshot 确认标题，或传 args 指定文件");
        }
    }


    private LaunchOrPlaceView BuildView(LaunchState s)
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
            dialogs);
    }

}
