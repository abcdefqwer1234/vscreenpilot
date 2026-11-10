using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Core.Displays;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public JsonObject LaunchOrPlaceApp(
        string? path,
        string? windowTitle,
        string? arguments,
        int waitMs,
        bool newInstance,
        bool ensureDocument = true)
    {
        DisplayInfo display = EnsureEnvironment();
        // 在启动任何进程之前记录用户当前窗口：新窗口出现时往往已经抢到前台。
        IntPtr userForeground = WindowManager.GetForegroundWindow();
        WindowInfo? window;
        bool started = false;
        bool closeOnRelease = false;
        bool dedicated = false;
        bool hadRunningInstance = false;
        string? tempProfile = null;
        string exeName = string.Empty;

        if (!string.IsNullOrWhiteSpace(windowTitle))
        {
            window = WindowManager.FindByTitle(windowTitle)
                ?? throw new ToolException("E_WINDOW_NOT_FOUND", $"找不到标题匹配的窗口: {windowTitle}");

            // 显式指定窗口时按接管处理，不创建新实例。
            newInstance = false;
        }
        else if (!string.IsNullOrWhiteSpace(path))
        {
            EnsureAppAllowed(path);

            if (Path.IsPathRooted(path) && !File.Exists(path))
            {
                throw new ToolException("E_APP_START", $"应用不存在: {path}");
            }

            exeName = Path.GetFileNameWithoutExtension(path);

            if (newInstance)
            {
                using (Process? probe = FindRunningProcess(path))
                {
                    hadRunningInstance = probe is not null;
                }

                var before = new HashSet<IntPtr>(
                    WindowManager.ListTopLevel().Select(item => item.Hwnd));

                var startInfo = new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
                };

                startInfo.Arguments = BuildNewInstanceArguments(exeName, arguments, out tempProfile);

                Process process = Process.Start(startInfo)
                    ?? throw new ToolException("E_APP_START", $"无法启动应用: {path}");

                int processId = process.Id;
                started = true;

                using (process)
                {
                    if (!_job.Assign(process))
                    {
                        _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                    }

                    window = WaitForNewWindow(process, exeName, before, waitMs);
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
                            _log.Warn($"无法创建独立实例，已接管现有窗口 pid={reuse.Pid} \"{reuse.Title}\"");
                            window = reuse;
                            newInstance = false;
                            closeOnRelease = false;
                            dedicated = false;
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

                closeOnRelease = true;
                dedicated = !hadRunningInstance || exeName.Equals("msedge", StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                Process? running = FindRunningProcess(path);
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

                    var startInfo = new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = false,
                        WorkingDirectory = Path.GetDirectoryName(path) ?? Environment.CurrentDirectory,
                    };

                    if (!string.IsNullOrWhiteSpace(arguments))
                    {
                        startInfo.Arguments = arguments;
                    }

                    Process process = Process.Start(startInfo)
                        ?? throw new ToolException("E_APP_START", $"无法启动应用: {path}");

                    int processId = process.Id;
                    started = true;

                    using (process)
                    {
                        if (!_job.Assign(process))
                        {
                            _log.Warn($"应用 {processId} 未能加入作业对象，崩溃兜底不可用");
                        }

                        window = WaitForNewWindow(process, exeName, before, waitMs);
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

        TrackedApp? tracked;
        lock (_gate)
        {
            tracked = _apps.FirstOrDefault(app => app.Hwnd == window.Hwnd)
                ?? _apps.FirstOrDefault(app => app.Pid == window.Pid);
            if (tracked is null)
            {
                tracked = new TrackedApp
                {
                    Pid = window.Pid,
                    ExePath = WindowManager.GetProcessPath(window.Pid),
                    Hwnd = window.Hwnd,
                    Title = window.Title,
                    StartedProcess = started,
                    CloseWindowOnRelease = closeOnRelease,
                    DedicatedProcess = dedicated,
                    TempProfile = tempProfile,
                    Placement = WindowManager.GetPlacement(window.Hwnd),
                    PlacementSaved = true,
                };
                _apps.Add(tracked);
                _log.Info(
                    $"接管应用 pid={window.Pid} \"{window.Title}\" started={started} newInstance={newInstance}");
            }
            else
            {
                tracked.Hwnd = window.Hwnd;
                tracked.Title = window.Title;
                tracked.CloseWindowOnRelease |= closeOnRelease;
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

        bool word = exeName.Equals("winword", StringComparison.OrdinalIgnoreCase);
        bool documentReady = !word;
        var warnings = new JsonArray();

        if (word)
        {
            documentReady = Uia.FindElement(window.Hwnd, null, "Document", null, null) is not null;

            // 仅对本会话新启动的专用 Word 自动新建文档；复用用户 Word 时不动其文档状态。
            if (!documentReady && ensureDocument && started && closeOnRelease)
            {
                PauseFocusGuard();

                try
                {
                    if (tracked is not null && EnsureForeground(tracked))
                    {
                        InputInjector.PressKeys("Ctrl+N");
                        documentReady = WaitForWordDocument(window.Hwnd, 6000);
                    }
                }
                finally
                {
                    RestoreForeground(previousForeground);
                    ResumeFocusGuard();
                }
            }

            if (!documentReady)
            {
                warnings.Add("Word 当前没有可编辑文档；可传 ensure_document=true 或手动 Ctrl+N");
            }
        }

        var result = new JsonObject
        {
            ["launched"] = started,
            ["new_instance"] = newInstance,
            ["reused"] = !started,
            ["maximized"] = maximized,
            ["close_on_release"] = tracked.CloseWindowOnRelease,
            ["hwnd"] = window.Hwnd.ToInt64(),
            ["pid"] = window.Pid,
            ["title"] = after?.Title ?? window.Title,
            ["document_ready"] = documentReady,
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
            result["hint"] = tracked.CloseWindowOnRelease
                ? "已复用本会话已启动的窗口；release_environment 会关闭它"
                : "已接管现有窗口；关闭调试后只归位、不关闭该应用";
        }

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

    private static Process? FindRunningProcess(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
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
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return;
            }

            if (_job.Assign(process))
            {
                _log.Info($"已将进程 {pid} 加入作业对象");
            }
            else
            {
                _log.Warn($"进程 {pid} 未能加入作业对象，崩溃兜底不可用");
            }
        }
        catch
        {
            // 进程可能在分配前已经退出/转交。
        }
    }

    private WindowInfo? WaitForNewWindow(
        Process process, string exeName, HashSet<IntPtr> before, int waitMs)
    {
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

            Thread.Sleep(100);
        }

        return null;
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
                _log.Info(
                    $"窗口更新: hwnd=0x{window.Hwnd.ToInt64():X} area={window.Area} " +
                    $"title=\"{window.Title}\" small={smallWindow}");
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

        _log.Info(
            $"等待窗口稳定结束: hwnd=0x{final.Hwnd.ToInt64():X} area={final.Area} " +
            $"title=\"{final.Title}\"");

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
