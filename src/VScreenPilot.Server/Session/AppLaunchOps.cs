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
    string? Path, string? WindowTitle, string? Arguments, int? Pid, LaunchOptions Options);

/// <summary>launch_or_place_app 的行为选项。</summary>
internal sealed record LaunchOptions(int WaitMs, bool NewInstance, bool EnsureDocument);

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
        int? pid = request.Pid;
        var s = new LaunchState
        {
            Path = path,
            WindowTitle = windowTitle,
            Arguments = arguments,
            WaitMs = waitMs,
            NewInstance = newInstance,
            EnsureDocument = ensureDocument,
            Pid = pid,
            Display = _core._apps.EnsureDisplay(),
            UserForeground = WindowManager.GetForegroundWindow(),
        };

        ResolveTarget(s);
        _core.LaunchFlowOps.TrackWindow(s);
        PresentWindow(s);
        _core.LaunchFlowOps.EnsureDocumentReady(s);
        return BuildView(s);
    }

    internal void ResolveTarget(LaunchState s)
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
            using (running)
            {
                s.Window = _core.WindowOps.WaitForMainWindow(running, s.WaitMs);
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

            Process process = Process.Start(startInfo)
                ?? throw new ToolException("E_APP_START", $"无法启动应用: {s.LaunchPath}");

            int processId = process.Id;
            s.Started = true;

            using (process)
            {
                if (!_core._session.Job.Assign(process))
                {
                    _core._log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                }

                int launchedContentPid;
                string launchedContentPath;
                s.Window = _core.WindowOps.WaitForNewWindow(
                    process, s.ExeName, before, s.WaitMs, out launchedContentPid, out launchedContentPath);
                s.ContentPid = launchedContentPid;
                s.ContentPath = launchedContentPath;
            }

            if (s.Window is null)
            {
                AppTermination.KillProcessTree(processId);
                throw new ToolException("E_WINDOW_TIMEOUT", "等待新窗口超时");
            }

            s.CloseOnRelease = true;
            s.Dedicated = true;
        }
    }


    internal void PresentWindow(LaunchState s)
    {
        s.PreviousForeground = s.UserForeground;
        WindowManager.MoveAndMaximize(s.Window!.Hwnd, s.Display.X, s.Display.Y, s.Display.Width, s.Display.Height);
        // 应用启动/会话恢复可能在稍后多次激活自己；同步归还一次，再持续观察一小段时间。
        Thread.Sleep(500);
        _core._focus.RestoreForeground(s.PreviousForeground);
        _core._focus.StartFocusGuard(s.PreviousForeground);
        s.Maximized = WindowManager.WaitForZoomed(s.Window!.Hwnd, TimeSpan.FromSeconds(2));
        Thread.Sleep(300);
        s.After = WindowManager.FromHandle(s.Window!.Hwnd);
        _core._session.SaveState();


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
            dialogs);
    }
}
