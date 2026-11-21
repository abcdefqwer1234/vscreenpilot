using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public JsonObject LaunchOrPlaceApp(
        string? path,
        string? windowTitle,
        string? arguments,
        int waitMs,
        bool newInstance,
        bool ensureDocument = true,
        int? pid = null)
    {
        DisplayInfo display = EnsureEnvironment();
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
            string launchPath = hasDirectory ? _paths.Resolve(path) : path;
            EnsureAppAllowed(launchPath);

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
                        ? Clients.CurrentCwd
                        : workingDirectory,
                };

                startInfo.Arguments = BuildNewInstanceArguments(exeName, arguments, out tempProfile);

                Process process = Process.Start(startInfo)
                    ?? throw new ToolException("E_APP_START", $"无法启动应用: {launchPath}");

                int processId = process.Id;
                started = true;

                using (process)
                {
                    if (!_job.Assign(process))
                    {
                        _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                    }

                    window = WaitForNewWindow(
                        process, exeName, before, waitMs, out contentPid, out contentPath);
                }

                if (window is null)
                {
                    KillProcessTree(processId);
                    if (tempProfile is not null)
                    {
                        TryDeleteDirectory(tempProfile);
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
                            ? Clients.CurrentCwd
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
                        if (!_job.Assign(process))
                        {
                            _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                        }

                        window = WaitForNewWindow(
                            process, exeName, before, waitMs, out contentPid, out contentPath);
                    }

                    if (window is null)
                    {
                        KillProcessTree(processId);
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
        EnsureAppAllowed(allowPath);

        TrackedApp? tracked;
        lock (_gate)
        {
            tracked = _apps.FirstOrDefault(app => IsOwned(app) && app.Hwnd == window.Hwnd)
                ?? _apps.FirstOrDefault(app => IsOwned(app) && app.Pid == window.Pid);

            TrackedApp? foreign = _apps.FirstOrDefault(app =>
                !IsOwned(app) && (app.Hwnd == window.Hwnd || app.Pid == window.Pid));

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
                        ["owner_cwd"] = Clients.FindCwd(foreign.OwnerClientId),
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
                    OwnerClientId = Clients.CurrentId,
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
                _apps.Add(tracked);
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
        RestoreForeground(previousForeground);
        StartFocusGuard(previousForeground);
        bool maximized = WindowManager.WaitForZoomed(window.Hwnd, TimeSpan.FromSeconds(2));
        Thread.Sleep(300);
        WindowInfo? after = WindowManager.FromHandle(window.Hwnd);
        SaveState();

        var warnings = new JsonArray();

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
            documentReady = Uia.FindElement(window.Hwnd, null, "Document", null, null) is not null;
            documentReason = documentReady == true ? "document_element" : "no_document_element";

            // 仅对本会话新启动的专用 Word 自动新建文档；复用用户 Word 时不动其文档状态。
            if (documentReady != true && ensureDocument && started && closeOnRelease)
            {
                PauseFocusGuard();

                try
                {
                    if (tracked is not null && EnsureForeground(tracked))
                    {
                        InputInjector.PressKeys("Ctrl+N");
                        documentReady = WaitForWordDocument(window.Hwnd, 6000);
                        documentReason = documentReady == true ? "created_by_ctrl_n" : "ctrl_n_failed";
                    }
                }
                finally
                {
                    RestoreForeground(previousForeground);
                    ResumeFocusGuard();
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
                PauseFocusGuard();

                try
                {
                    if (EnsureForeground(tracked))
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
                    RestoreForeground(previousForeground);
                    ResumeFocusGuard();
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

        var result = new JsonObject
        {
            ["launched"] = started,
            ["new_instance"] = newInstance,
            ["reused"] = !started,
            ["maximized"] = maximized,
            ["close_on_release"] = tracked?.CloseWindowOnRelease ?? closeOnRelease,
            ["hwnd"] = window.Hwnd.ToInt64(),
            ["pid"] = window.Pid,
            ["title"] = after?.Title ?? window.Title,
            ["document_ready"] = documentReady is null ? null : JsonValue.Create(documentReady.Value),
            ["document_ready_reason"] = documentReason,
            ["ensure_document"] = ensureDocument,
            ["bounds"] = after is null
                ? new JsonObject()
                : new JsonObject
                {
                    ["x"] = after.Rect.Left,
                    ["y"] = after.Rect.Top,
                    ["width"] = after.Width,
                    ["height"] = after.Height,
                },
        };

        if (warnings.Count > 0)
        {
            result["warnings"] = warnings;
        }

        if (tempProfile is not null)
        {
            result["profile"] = tempProfile;
        }

        if (!newInstance && hadRunningInstance)
        {
            result["hint"] = tracked?.CloseWindowOnRelease == true
                ? "已复用本会话已启动的窗口；release_environment 会关闭它"
                : "已接管现有窗口；关闭调试后只归位、不关闭该应用";
        }

        // 接管时就把“主窗口之外的 owned 窗口”暴露出来：Office 的自定义对话框、
        // 经典 #32770、#32768 菜单都不在主窗口 UIA 树里，模型需要先知道它们存在。
        var dialogs = new JsonArray();

        foreach (WindowInfo dialog in WindowManager.ListOwnedWindows(window.Pid, window.Hwnd))
        {
            dialogs.Add((JsonNode)new JsonObject
            {
                ["hwnd"] = dialog.Hwnd.ToInt64(),
                ["title"] = dialog.Title,
                ["class"] = dialog.ClassName,
            });
        }

        result["dialogs"] = dialogs;

        return result;
    }

    private bool WaitForWordDocument(IntPtr hwnd, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            try
            {
                if (Uia.FindElement(hwnd, null, "Document", null, null) is not null)
                {
                    return true;
                }
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Word 正在切换文档，稍后重试。
            }

            Thread.Sleep(150);
        }

        return false;
    }

    /// <summary>Office 窗口标题在打开文档后是“文件名 - 产品名”；开始屏幕只有产品名。</summary>
    internal static bool LooksLikeDocumentWindow(string title, string product)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        string trimmed = title.Trim();
        return trimmed.EndsWith(" - " + product, StringComparison.OrdinalIgnoreCase) &&
               trimmed.Length > product.Length + 3;
    }

    private static bool WaitForDocumentTitle(IntPtr hwnd, string product, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, timeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return false;
            }

            if (LooksLikeDocumentWindow(WindowManager.GetTitle(hwnd), product))
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return false;
    }

    private UiaElementInfo? FindBlankPresentationTile(IntPtr hwnd)
    {
        try
        {
            foreach (UiaElementInfo info in Uia.FindElements(
                         hwnd, null, UiaNameMatch.Exact, "Button", null, 200))
            {
                if (info.Name.Contains("空白", StringComparison.OrdinalIgnoreCase) ||
                    info.Name.Contains("Blank", StringComparison.OrdinalIgnoreCase))
                {
                    return info;
                }
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // 开始屏幕的 UIA 提供程序不稳定时按“找不到磁贴”处理。
        }

        return null;
    }

    /// <summary>该窗口是否已由当前客户端接管；用于区分“用户原有窗口”和“本会话早前启动的窗口”。</summary>
    private bool IsOwnedWindowOnCurrentClient(IntPtr hwnd)
    {
        lock (_gate)
        {
            return _apps.Any(app => IsOwned(app) && app.Hwnd == hwnd && IsLiveTrackedApp(app));
        }
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

    /// <summary>
    /// Win11 记事本会恢复上次会话的标签：无参数启动时若标题带文件名，提示调用方先确认。
    /// 只在本次确实启动了新进程时判断，接管旧窗口的场景由 launchWarning 覆盖。
    /// </summary>
    internal static bool LooksLikeRestoredNotepadTitle(
        string exeName, bool started, string? arguments, string? title)
    {
        if (!started ||
            !exeName.Equals("notepad", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(arguments) ||
            string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        // 空白标签的标题是“无标题 - 记事本”/“Untitled - Notepad”。
        if (title.Contains("无标题", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Untitled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return title.Contains("记事本", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Notepad", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildNewInstanceArguments(
        string exeName, string? arguments, out string? tempProfile)
    {
        tempProfile = null;
        string name = exeName.ToLowerInvariant();

        if (name == "msedge")
        {
            string profile = Path.Combine(
                Path.GetTempPath(), "VScreenPilot", "profiles", "edge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            tempProfile = profile;

            string profileArgs = $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check";
            return string.IsNullOrWhiteSpace(arguments) ? profileArgs : profileArgs + " " + arguments;
        }

        if (name == "winword")
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "/n";
            }

            return arguments.Contains("/n", StringComparison.OrdinalIgnoreCase)
                ? arguments
                : "/n " + arguments;
        }

        return arguments ?? string.Empty;
    }

    private Process? FindRunningProcess(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = _paths.Resolve(path);
        }
        catch
        {
            fullPath = path;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch
        {
            return null;
        }

        Process? fullMatch = null;
        Process? nameFallback = null;
        Process? pathFallback = null;

        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    bool samePath = IsSameExecutable(process, fullPath);
                    bool hasWindow = process.MainWindowHandle != IntPtr.Zero;

                    if (samePath && hasWindow)
                    {
                        fullMatch ??= process;
                    }
                    else if (samePath)
                    {
                        pathFallback ??= process;
                    }
                    else if (hasWindow)
                    {
                        nameFallback ??= process;
                    }
                }
                catch
                {
                    nameFallback ??= process;
                }
            }

            // 同名但不同路径的进程只作为最后兜底，避免误接管其他位置的同名程序。
            Process? chosen = fullMatch ?? pathFallback ?? nameFallback;

            foreach (Process process in processes)
            {
                if (!ReferenceEquals(process, chosen))
                {
                    process.Dispose();
                }
            }

            return chosen;
        }
        catch
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }

            return null;
        }
    }

    private static bool IsSameExecutable(Process process, string fullPath)
    {
        try
        {
            string? modulePath = process.MainModule?.FileName;
            return modulePath is not null &&
                   string.Equals(Path.GetFullPath(modulePath), fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void AssignWindowProcessToJob(int pid)
    {
        // 启动壳进程会转交给真实进程；重试几次再判定失败，避免把正常的交接记成兜底不可用。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);

                if (process.HasExited)
                {
                    Thread.Sleep(150);
                    continue;
                }

                if (_job.Assign(process))
                {
                    _log.Info($"已将进程 {pid} 加入作业对象");
                    return;
                }

                Thread.Sleep(150);
            }
            catch
            {
                // 进程可能在分配前已经退出/转交。
                Thread.Sleep(150);
            }
        }

        if (!_jobUnavailableLogged)
        {
            _jobUnavailableLogged = true;
            _log.Warn(
                $"进程 {pid} 未能加入作业对象（{_job.Status}）；崩溃兜底不可用，" +
                "将依赖进程树清理与启动时孤儿清扫");
        }
    }

    private WindowInfo? WaitForNewWindow(
        Process process, string exeName, HashSet<IntPtr> before, int waitMs,
        out int contentPid, out string contentPath)
    {
        contentPid = 0;
        contentPath = string.Empty;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, waitMs));

        while (DateTime.UtcNow < deadline)
        {
            List<WindowInfo> candidates = WindowManager.ListTopLevel()
                .Where(item => item.Visible &&
                               !before.Contains(item.Hwnd) &&
                               item.Area >= 100 * 80 &&
                               !string.IsNullOrWhiteSpace(item.Title))
                .ToList();

            WindowInfo? byPid = candidates
                .Where(item => item.Pid == process.Id)
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (byPid is not null)
            {
                Thread.Sleep(300);
                return WindowManager.FromHandle(byPid.Hwnd) ?? byPid;
            }

            WindowInfo? byName = candidates
                .Where(item => string.Equals(
                    GetProcessName(item.Pid), exeName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (byName is not null)
            {
                Thread.Sleep(300);
                return WindowManager.FromHandle(byName.Hwnd) ?? byName;
            }

            // UWP：calc.exe 只是启动器，真正窗口属于 ApplicationFrameWindow。
            // 先认内容进程与 exe 匹配的宿主（可复用已存在的 frame），
            // 再退化为“新出现的 ApplicationFrameWindow 窗口”。
            WindowInfo? byUwp = candidates
                .Where(item => item.ClassName.Contains(
                    "ApplicationFrame", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (byUwp is not null)
            {
                (contentPid, contentPath) = ResolveUwpContentProcess(byUwp.Hwnd);
                Thread.Sleep(300);
                return WindowManager.FromHandle(byUwp.Hwnd) ?? byUwp;
            }

            Thread.Sleep(100);
        }

        // 超时前再做一次 UIA 探测：UWP 内容进程没有独立 HWND，
        // 只能从 frame 的语义树里找 ProcessId。
        UwpWindow? uwp = FindUwpWindowByUia(exeName);

        if (uwp is not null)
        {
            contentPid = uwp.ContentPid;
            contentPath = uwp.ContentPath;
            return uwp.Window;
        }

        return null;
    }

    private sealed record UwpWindow(WindowInfo Window, int ContentPid, string ContentPath);

    /// <summary>
    /// UWP 的顶层是 ApplicationFrameWindow，内容进程（CalculatorApp 等）没有独立 HWND。
    /// 用 UIA 树里的 ProcessId 反查 exe，避免把别的 UWP 窗口当成目标。
    /// </summary>
    private UwpWindow? FindUwpWindowByUia(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return null;
        }

        foreach (WindowInfo candidate in WindowManager.ListTopLevel())
        {
            if (!candidate.Visible || !IsApplicationFrame(candidate.Hwnd))
            {
                continue;
            }

            (int pid, string path) = ResolveUwpContentProcess(candidate.Hwnd);

            if (pid > 0 &&
                Path.GetFileNameWithoutExtension(path)
                    .Contains(exeName, StringComparison.OrdinalIgnoreCase))
            {
                return new UwpWindow(candidate, pid, path);
            }
        }

        return null;
    }

    /// <summary>pid 接管时 UWP 内容进程没有顶层窗口，反查承载它的 frame。</summary>
    private WindowInfo? FindUwpFrameForProcess(int pid)
    {
        foreach (WindowInfo candidate in WindowManager.ListTopLevel())
        {
            if (!candidate.Visible || !IsApplicationFrame(candidate.Hwnd))
            {
                continue;
            }

            (int contentPid, _) = ResolveUwpContentProcess(candidate.Hwnd);

            if (contentPid == pid)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsApplicationFrame(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);
        return className.ToString().Contains("ApplicationFrame", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从 frame 的 UIA 树里找非 frame-host 的内容进程。</summary>
    private (int Pid, string Path) ResolveUwpContentProcess(IntPtr frameHwnd)
    {
        if (frameHwnd == IntPtr.Zero || !IsApplicationFrame(frameHwnd))
        {
            return (0, string.Empty);
        }

        UiaTreeResult tree = Uia.BuildTree(
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

    private static void CollectProcessIds(UiaTreeNode node, HashSet<int> processIds)
    {
        processIds.Add(node.Info.ProcessId);

        foreach (UiaTreeNode child in node.Children)
        {
            CollectProcessIds(child, processIds);
        }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private WindowInfo? WaitForMainWindow(Process process, int waitMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, waitMs));
        WindowInfo? found = null;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new ToolException("E_APP_EXITED", $"应用在窗口就绪前退出 (exit={process.ExitCode})");
            }

            found = WindowManager.FindMainWindow(process.Id);
            if (found is not null)
            {
                break;
            }

            Thread.Sleep(100);
        }

        if (found is null)
        {
            throw new ToolException(
                "E_WINDOW_TIMEOUT",
                $"等待主窗口超时 ({waitMs}ms, pid={process.Id})");
        }

        Thread.Sleep(400);
        return WindowManager.FindMainWindow(process.Id) ?? found;
    }

    private WindowInfo? WaitForStableWindow(WindowInfo window, int waitMs, int displayArea)
    {
        // 启动 splash/引导页通常远小于主窗口。小于虚拟屏 10% 的窗口先不当作主窗口，
        // 继续轮询同进程的最大窗口，直到出现更大的窗口或超时。
        // 启动 splash 的窗口面积可能并不小（CCW 的 splash 约 920×460），
        // 用“不足虚拟屏一半”作为启动期窗口的判定，最多观察 12 秒等待主窗口。
        bool smallWindow = displayArea > 0 && window.Area < displayArea * 0.50;
        var deadline = DateTime.UtcNow.AddMilliseconds(
            smallWindow
                ? Math.Max(3000, Math.Min(waitMs, 20000))
                : Math.Max(500, Math.Min(waitMs, 3000)));

        _log.Info(
            $"等待窗口稳定: hwnd=0x{window.Hwnd.ToInt64():X} area={window.Area} " +
            $"displayArea={displayArea} small={smallWindow} waitMs={waitMs}");

        int stable = 0;
        long lastArea = window.Area;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(150);

            // 不能用 FindMainWindow 的 owner 过滤：部分应用的 splash 是主窗口的
            // owner，会导到主窗口被排除、一直选中 splash。这里直接按 pid + 面积选。
            WindowInfo? next = WindowManager.ListTopLevel()
                .Where(item => item.Pid == window.Pid &&
                               item.Visible &&
                               item.Area >= 100 * 80 &&
                               !string.IsNullOrWhiteSpace(item.Title))
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (next is null)
            {
                // 启动 splash 关闭后到主窗口出现之间可能没有可见窗口；
                // 只要进程还活着就继续等，别把已失效的 splash 当成主窗口返回。
                if (!IsProcessAlive(window.Pid))
                {
                    break;
                }

                _log.Info($"等待窗口稳定: 暂无可选窗口，继续等待 pid={window.Pid}");
                continue;
            }

            if (next.Hwnd != window.Hwnd || next.Area > lastArea * 1.1 || next.Area < lastArea * 0.9)
            {
                window = next;
                lastArea = next.Area;
                stable = 0;
                smallWindow = displayArea > 0 && window.Area < displayArea * 0.50;
                // 不记录标题：可能含文档名/客户名。
                _log.Info(
                    $"窗口更新: hwnd=0x{window.Hwnd.ToInt64():X} area={window.Area} small={smallWindow}");
                continue;
            }

            stable++;
            if (!smallWindow && stable >= 2)
            {
                _log.Info($"窗口已稳定: hwnd=0x{window.Hwnd.ToInt64():X} area={window.Area}");
                return window;
            }
        }

        WindowInfo? final = WindowManager.ListTopLevel()
            .Where(item => item.Pid == window.Pid && item.Visible)
            .OrderByDescending(item => item.Area)
            .FirstOrDefault() ?? window;

        _log.Info($"等待窗口稳定结束: hwnd=0x{final.Hwnd.ToInt64():X} area={final.Area}");

        return final;
    }

    private static void CleanupStaleProfiles()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "VScreenPilot", "profiles");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string directory in Directory.GetDirectories(root, "edge-*"))
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    TryDeleteDirectory(directory);
                }
            }
        }
        catch
        {
            // 清理失败不影响启动。
        }
    }
}
