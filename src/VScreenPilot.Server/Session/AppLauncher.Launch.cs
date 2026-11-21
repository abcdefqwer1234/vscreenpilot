using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
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
        DisplayInfo display = _host.EnsureDisplay();
        // 在启动任何进程之前记录用户当前窗口：新窗口出现时往往已经抢到前台。
        IntPtr userForeground = WindowManager.GetForegroundWindow();
        WindowInfo? window;
        bool started = false;
        // UWP：真实内容进程（CalculatorApp 等）与 frame host 不同，关闭/白名单必须用前者。
        int contentPid = 0;
        string contentPath = string.Empty;
        bool closeOnRelease = false;
        bool dedicated = false;
        bool hadRunningInstance = false;
        bool adoptedExisting = false;
        string? launchWarning = null;
        string? tempProfile = null;
        string exeName = string.Empty;

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            IReadOnlyList<WindowInfo> candidates = WindowManager.FindByTitleCandidates(windowTitle);

            if (candidates.Count == 0)
            {
                throw new ToolException("E_WINDOW_NOT_FOUND", $"找不到标题匹配的窗口: {windowTitle}");
            }

            if (candidates.Count > 1)
            {
                // 只有唯一完全相等时才继续；否则把候选返回给模型，避免误接管用户窗口。
                List<WindowInfo> exact = candidates
                    .Where(item => item.Title.Equals(windowTitle, StringComparison.OrdinalIgnoreCase))
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
                            ["title"] = windowTitle,
                            ["windows"] = list.DeepClone(),
                            ["candidates"] = list,
                        },
                        retryable: true,
                        hint: "传更完整的标题，或改用 pid 精确指定（details.windows 给出候选）",
                        stage: "window");
                }

                candidates = exact;
            }

            window = candidates[0];

            // 显式指定窗口时按接管处理，不创建新实例。
            newInstance = false;
        }
        else if (pid is not null)
        {
            // 按 pid 接管是最精确的方式：UWP/单实例/多标签都绕开了标题歧义。
            // 普通应用有主窗口；UWP 的内容进程没有顶层窗口，只能反查 frame host。
            window = WindowManager.FindMainWindow(pid.Value)
                ?? FindUwpFrameForProcess(pid.Value)
                ?? throw new ToolException(
                    "E_WINDOW_NOT_FOUND",
                    $"pid={pid} 没有可见主窗口",
                    new JsonObject { ["pid"] = pid });

            newInstance = false;
            exeName = GetProcessName(pid.Value);

            if (window.Pid != pid.Value)
            {
                contentPid = pid.Value;
                contentPath = WindowManager.GetProcessPath(pid.Value);
            }
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            bool hasDirectory = Path.IsPathRooted(path) ||
                                path.Contains('\\') ||
                                path.Contains('/');
            string launchPath = hasDirectory ? _host.ResolvePath(path) : path;
            _host.EnsureAppAllowed(launchPath);

            if (File.Exists(launchPath) == false && hasDirectory)
            {
                throw new ToolException("E_APP_START", $"应用不存在: {launchPath}");
            }

            exeName = Path.GetFileNameWithoutExtension(launchPath);

            if (newInstance)
            {
                using (Process? probe = FindRunningProcess(launchPath))
                {
                    hadRunningInstance = probe is not null;
                }

                var before = new HashSet<IntPtr>(
                    WindowManager.ListTopLevel().Select(item => item.Hwnd));

                string? workingDirectory = Path.GetDirectoryName(launchPath);
                var startInfo = new ProcessStartInfo
                {
                    FileName = launchPath,
                    UseShellExecute = false,
                    WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                        ? _host.Clients.CurrentCwd
                        : workingDirectory,
                };

                startInfo.Arguments = BuildNewInstanceArguments(exeName, arguments, out tempProfile);

                Process process = Process.Start(startInfo)
                    ?? throw new ToolException("E_APP_START", $"无法启动应用: {launchPath}");

                int processId = process.Id;
                started = true;

                using (process)
                {
                    if (!_host.Job.Assign(process))
                    {
                        _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                    }

                    window = WaitForNewWindow(
                        process, exeName, before, waitMs, out contentPid, out contentPath);
                }

                if (window is null)
                {
                    AppReleaser.KillProcessTree(processId);
                    if (tempProfile is not null)
                    {
                        AppReleaser.TryDeleteDirectory(tempProfile);
                    }

                    if (hadRunningInstance)
                    {
                        // 单实例应用（如 Win11 记事本）无法创建新窗口：自动降级为接管现有窗口。
                        WindowInfo? reuse = null;

                        using (Process? running = FindRunningProcess(path))
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
                            window = reuse;
                            newInstance = false;
                            started = false;

                            if (!alreadyOwned)
                            {
                                // 用户原本就有的窗口：不能算本会话启动，release 只归位、不关闭。
                                closeOnRelease = false;
                                dedicated = false;
                                adoptedExisting = true;
                                launchWarning =
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
                                    ["app"] = exeName,
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
                (closeOnRelease, dedicated) = ResolveNewInstanceOwnership(
                    adoptedExisting,
                    hadRunningInstance,
                    exeName.Equals("msedge", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                Process? running = FindRunningProcess(launchPath);
                hadRunningInstance = running is not null;

                if (running is not null)
                {
                    using (running)
                    {
                        window = WaitForMainWindow(running, waitMs);
                    }

                    dedicated = false;
                }
                else
                {
                    var before = new HashSet<IntPtr>(
                        WindowManager.ListTopLevel().Select(item => item.Hwnd));

                    string? workingDirectory = Path.GetDirectoryName(launchPath);
                    var startInfo = new ProcessStartInfo
                    {
                        FileName = launchPath,
                        UseShellExecute = false,
                        WorkingDirectory = string.IsNullOrEmpty(workingDirectory)
                            ? _host.Clients.CurrentCwd
                            : workingDirectory,
                    };

                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        startInfo.Arguments = arguments;
                    }

                    Process process = Process.Start(startInfo)
                        ?? throw new ToolException("E_APP_START", $"无法启动应用: {launchPath}");

                    int processId = process.Id;
                    started = true;

                    using (process)
                    {
                        if (!_host.Job.Assign(process))
                        {
                            _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                        }

                        window = WaitForNewWindow(
                            process, exeName, before, waitMs, out contentPid, out contentPath);
                    }

                    if (window is null)
                    {
                        AppReleaser.KillProcessTree(processId);
                        throw new ToolException("E_WINDOW_TIMEOUT", "等待新窗口超时");
                    }

                    closeOnRelease = true;
                    dedicated = true;
                }
            }
        }
        else
        {
            throw new ToolException("E_INVALID_ARG", "path 与 window 至少提供一个");
        }

        if (window is null)
        {
            throw new ToolException("E_WINDOW_TIMEOUT", "等待应用主窗口超时");
        }

        // 启动壳进程可能已把窗口交接给新进程；以 hwnd 的真实属主为准。
        window = WindowManager.FromHandle(window.Hwnd) ?? window;

        window = WaitForStableWindow(window, waitMs, display.Width * display.Height) ?? window;

        // UWP 的 frame host 路径不是用户要启动的应用；内容进程解析失败时才退回 frame。
        if (contentPid == 0)
        {
            (contentPid, contentPath) = ResolveUwpContentProcess(window.Hwnd);
        }

        // path 启动时已校验；pid/window 接管也必须校验真实 exe，避免绕过 VSP_APP_ALLOWLIST（R24）。
        string allowPath = string.IsNullOrEmpty(contentPath)
            ? WindowManager.GetProcessPath(window.Pid)
            : contentPath;
        _host.EnsureAppAllowed(allowPath);

        TrackedApp? tracked;
        lock (_gate)
        {
            tracked = _host.Apps.FirstOrDefault(app => _host.IsOwned(app) && app.Hwnd == window.Hwnd)
                ?? _host.Apps.FirstOrDefault(app => _host.IsOwned(app) && app.Pid == window.Pid);

            TrackedApp? foreign = _host.Apps.FirstOrDefault(app =>
                !_host.IsOwned(app) && (app.Hwnd == window.Hwnd || app.Pid == window.Pid));

            if (tracked is null && foreign is not null)
            {
                throw new ToolException(
                    "E_APP_OWNED",
                    $"窗口属于另一个客户端（client={foreign.OwnerClientId}）",
                    new JsonObject
                    {
                        ["hwnd"] = window.Hwnd.ToInt64(),
                        ["pid"] = window.Pid,
                        ["owner_client_id"] = foreign.OwnerClientId,
                        ["owner_cwd"] = _host.Clients.FindCwd(foreign.OwnerClientId),
                    },
                    retryable: true,
                    hint: "让该客户端 release_environment，或为当前客户端启动一个独立实例",
                    stage: "app");
            }

            if (tracked is null)
            {
                tracked = new TrackedApp
                {
                    Pid = window.Pid,
                    OwnerClientId = _host.Clients.CurrentId,
                    ExePath = WindowManager.GetProcessPath(window.Pid),
                    ContentPid = contentPid,
                    ContentExePath = contentPath,
                    Hwnd = window.Hwnd,
                    Title = window.Title,
                    StartedProcess = started,
                    Owned = started && closeOnRelease,
                    CloseWindowOnRelease = closeOnRelease,
                    DedicatedProcess = dedicated,
                    TempProfile = tempProfile,
                    Placement = WindowManager.GetPlacement(window.Hwnd),
                    PlacementSaved = true,
                };
                _host.Apps.Add(tracked);
                _log.Info(
                    $"接管应用 pid={window.Pid} started={started} newInstance={newInstance}");
            }
            else
            {
                tracked.Hwnd = window.Hwnd;
                tracked.Title = window.Title;
                tracked.Owned |= started && closeOnRelease;
                tracked.CloseWindowOnRelease |= closeOnRelease;

                if (contentPid > 0)
                {
                    tracked.ContentPid = contentPid;
                    tracked.ContentExePath = contentPath;
                }
            }
        }

        // 现代应用（Edge/Notepad）由 stub 转交给真实进程，早期分配的作业对象不覆盖它。
        if (started && dedicated)
        {
            AssignWindowProcessToJob(window.Pid);
        }

        IntPtr previousForeground = userForeground;
        WindowManager.MoveAndMaximize(window.Hwnd, display.X, display.Y, display.Width, display.Height);
        // 应用启动/会话恢复可能在稍后多次激活自己；同步归还一次，再持续观察一小段时间。
        Thread.Sleep(500);
        _host.RestoreForeground(previousForeground);
        _host.StartFocusGuard(previousForeground);
        bool maximized = WindowManager.WaitForZoomed(window.Hwnd, TimeSpan.FromSeconds(2));
        Thread.Sleep(300);
        WindowInfo? after = WindowManager.FromHandle(window.Hwnd);
        _host.SaveState();

        var warnings = new List<string>();

        if (launchWarning is not null)
        {
            warnings.Add(launchWarning);
        }
        else if (LooksLikeRestoredNotepadTitle(
                     exeName, started, arguments, after?.Title ?? window.Title))
        {
            warnings.Add(
                "记事本可能恢复了上次会话的标签；请先用 snapshot 确认标题，或传 args 指定文件");
        }
        bool word = exeName.Equals("winword", StringComparison.OrdinalIgnoreCase);
        bool powerpoint = exeName.Equals("powerpnt", StringComparison.OrdinalIgnoreCase);
        bool excel = exeName.Equals("excel", StringComparison.OrdinalIgnoreCase);
        bool? documentReady;
        string documentReason;

        if (word)
        {
            documentReady = _host.Uia.FindElement(window.Hwnd, null, "Document", null, null) is not null;
            documentReason = documentReady == true ? "document_element" : "no_document_element";

            // 仅对本会话新启动的专用 Word 自动新建文档；复用用户 Word 时不动其文档状态。
            if (documentReady != true && ensureDocument && started && closeOnRelease)
            {
                _host.PauseFocusGuard();

                try
                {
                    if (tracked is not null && _host.EnsureForeground(tracked))
                    {
                        InputInjector.PressKeys("Ctrl+N");
                        documentReady = WaitForWordDocument(window.Hwnd, 6000);
                        documentReason = documentReady == true ? "created_by_ctrl_n" : "ctrl_n_failed";
                    }
                }
                finally
                {
                    _host.RestoreForeground(previousForeground);
                    _host.ResumeFocusGuard();
                }
            }

            if (documentReady != true)
            {
                warnings.Add("Word 当前没有可编辑文档；可传 ensure_document=true 或手动 Ctrl+N");
            }
        }
        else if (powerpoint)
        {
            string currentTitle = WindowManager.GetTitle(window.Hwnd);
            documentReady = LooksLikeDocumentWindow(currentTitle, "PowerPoint");
            documentReason = documentReady == true ? "document_title" : "start_screen_or_no_document";

            if (documentReady != true && ensureDocument && started && closeOnRelease && tracked is not null)
            {
                _host.PauseFocusGuard();

                try
                {
                    if (_host.EnsureForeground(tracked))
                    {
                        InputInjector.PressKeys("Ctrl+N");
                        documentReady = WaitForDocumentTitle(window.Hwnd, "PowerPoint", 5000);

                        if (documentReady != true)
                        {
                            // 开始屏幕的“空白演示文稿”磁贴是普通按钮；按名字找，别猜坐标。
                            UiaElementInfo? tile = FindBlankPresentationTile(window.Hwnd);

                            if (tile is not null && tile.Width > 0 && tile.Height > 0)
                            {
                                InputInjector.Click(
                                    tile.Left + tile.Width / 2, tile.Top + tile.Height / 2,
                                    "left", clicks: 1);
                                documentReady = WaitForDocumentTitle(window.Hwnd, "PowerPoint", 8000);
                                documentReason = documentReady == true ? "blank_tile" : "blank_tile_failed";
                            }
                            else
                            {
                                // 部分 Office 版本的开始屏幕不把磁贴暴露给 UIA。
                                // 按 1280×720 的实测比例估算“空白演示文稿”磁贴中心。
                                int estimateX = display.X + (int)Math.Round(display.Width * 0.15625);
                                int estimateY = display.Y + (int)Math.Round(display.Height * 0.3125);
                                InputInjector.Click(estimateX, estimateY, "left", clicks: 1);
                                documentReady = WaitForDocumentTitle(window.Hwnd, "PowerPoint", 8000);
                                documentReason = documentReady == true
                                    ? "blank_tile_estimated"
                                    : "blank_tile_failed";
                            }
                        }
                        else
                        {
                            documentReason = "created_by_ctrl_n";
                        }
                    }
                }
                finally
                {
                    _host.RestoreForeground(previousForeground);
                    _host.ResumeFocusGuard();
                }

                if (documentReady != true)
                {
                    warnings.Add("PowerPoint 停在开始屏幕；请输入文件名/点“空白演示文稿”，或手动 Ctrl+N");
                }
            }
        }
        else if (excel)
        {
            documentReady = LooksLikeDocumentWindow(WindowManager.GetTitle(window.Hwnd), "Excel");
            documentReason = documentReady == true ? "document_title" : "no_workbook";
        }
        else
        {
            // 没有按应用建模时不要猜：返回 null，让调用方自己决定是否 snapshot。
            documentReady = null;
            documentReason = "not_modeled";
        }

        var dialogs = new List<LaunchDialogView>();

        foreach (WindowInfo dialog in WindowManager.ListOwnedWindows(window.Pid, window.Hwnd))
        {
            dialogs.Add(new LaunchDialogView(dialog.Hwnd.ToInt64(), dialog.Title, dialog.ClassName));
        }

        return new LaunchOrPlaceView(
            started,
            newInstance,
            !started,
            maximized,
            tracked?.CloseWindowOnRelease ?? closeOnRelease,
            window.Hwnd.ToInt64(),
            window.Pid,
            after?.Title ?? window.Title,
            documentReady,
            documentReason,
            ensureDocument,
            after is null
                ? new OptionalBoundsView(null, null, null, null)
                : new OptionalBoundsView(after.Rect.Left, after.Rect.Top, after.Width, after.Height),
            warnings.Count > 0 ? warnings : null,
            tempProfile,
            !newInstance && hadRunningInstance
                ? tracked?.CloseWindowOnRelease == true
                    ? "已复用本会话已启动的窗口；release_environment 会关闭它"
                    : "已接管现有窗口；关闭调试后只归位、不关闭该应用"
                : null,
            dialogs);
    }

}
