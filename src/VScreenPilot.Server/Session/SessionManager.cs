using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Core.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

internal sealed class TrackedApp
{
    public int Pid { get; set; }
    public string ExePath { get; init; } = string.Empty;
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; } = string.Empty;
    public bool StartedProcess { get; init; }
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    /// <summary>本会话真正拥有该窗口/进程时才允许在释放时关闭；接管用户窗口必须为 false。</summary>
    public bool Owned { get; set; }
    public bool CloseWindowOnRelease { get; set; }
    public bool DedicatedProcess { get; set; }
    public string? TempProfile { get; init; }
    public NativeMethods.WINDOWPLACEMENT Placement { get; set; }
    public bool PlacementSaved { get; set; }
}

internal sealed class FrameSnapshot
{
    public required string Id { get; init; }
    public required byte[] Bgra { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required int X { get; init; }
    public required int Y { get; init; }
}

/// <summary>
/// 会话生命周期：互斥体、按需挂载虚拟屏、窗口归位、统一清理。
/// </summary>
internal sealed partial class SessionManager : IDisposable
{
    public static readonly int VirtualWidth = ReadDisplaySetting("VSP_DISPLAY_WIDTH", 1280, 800, 3840);
    public static readonly int VirtualHeight = ReadDisplaySetting("VSP_DISPLAY_HEIGHT", 720, 600, 2160);
    public static readonly int VirtualRefresh = ReadDisplaySetting("VSP_DISPLAY_REFRESH", 60, 30, 240);

    private static int ReadDisplaySetting(string name, int fallback, int min, int max)
    {
        string? raw = Environment.GetEnvironmentVariable(name);

        return int.TryParse(raw, out int value) ? Math.Clamp(value, min, max) : fallback;
    }

    private readonly object _gate = new();
    private readonly LogRing _log;
    private readonly StateStore _stateStore;
    private readonly JobObject _job;
    private readonly VddDevice _vdd = new();
    private readonly List<TrackedApp> _apps = new();
    private readonly bool _allowScript;
    private readonly string[] _appAllowlist;

    private DisplayInfo? _virtualDisplay;
    private int _displayIndex = -1;
    private bool _initialized;
    private int _disposed;

    private readonly List<FrameSnapshot> _frames = new();
    private readonly object _frameLock = new();
    private long _nextFrameId;
    // 按区域保留更多基线，避免只截另一区域就把当前区域基线挤掉。
    private const int MaxFrames = 16;
    private readonly HashSet<int> _uiaBlockedPids = new();

    private UiaService? _uia;
    private CursorGuard? _cursorGuard;
    private CancellationTokenSource? _cancellationSource;
    private long _lastGeometryCheckTicks;
    private bool _selfHealed;
    private int _selfHealCount;
    private string? _lastSelfHealReason;
    private bool _adoptedOrphan;
    private int _displayRecreateCount;
    private int _vddReopenCount;

    private int _recoveredDisplayIndex = -1;
    private string _recoveredDisplayDevice = string.Empty;
    private int _recoveredPid;

    private volatile bool _focusGuardPaused;
    private volatile bool _focusGuardRunning;
    private readonly object _focusActionGate = new();
    private Thread? _focusGuardThread;
    private IntPtr _lastUserForeground;
    private long _nextFocusRestoreTicks;
    private long _focusGuardResumeAtTicks;

    private UiaService Uia => _uia ??= new UiaService();

    /// <summary>
    /// 放弃当前 UIA 服务并换新实例。只在 UIA 调用超时/疑似卡死时调用：
    /// 旧实例可能仍有线程卡在 COM 调用里，不能 Dispose，只能让新调用走新实例。
    /// </summary>
    public void ResetUia()
    {
        UiaService? old = Interlocked.Exchange(ref _uia, null);
        if (old is not null)
        {
            _log.Warn("UIA 服务疑似卡死，已切换到新实例（旧实例留给后台线程自行结束）");
        }
    }

    /// <summary>当前工具调用的取消令牌（由 MCP 层设置，供 wait_for 等长操作观察）。</summary>
    public CancellationToken Cancellation
    {
        get
        {
            CancellationTokenSource? source = _cancellationSource;
            if (source is null)
            {
                return CancellationToken.None;
            }

            try
            {
                return source.Token;
            }
            catch (ObjectDisposedException)
            {
                return CancellationToken.None;
            }
        }
    }

    public void SetCancellation(CancellationTokenSource? source) => _cancellationSource = source;

    public SessionManager(
        LogRing log, StateStore stateStore, JobObject job, bool allowScript = false)
    {
        _log = log;
        _stateStore = stateStore;
        _job = job;
        _allowScript = allowScript;
        _appAllowlist = ReadAppAllowlist();
        CleanupStaleProfiles();
        RecoverFromPreviousCrash();
    }

    public LogRing Log => _log;

    public bool AllowScript => _allowScript;

    public bool Initialized => _initialized;

    public DisplayInfo? VirtualDisplay => _virtualDisplay;

    public IReadOnlyList<TrackedApp> Apps => _apps;

    /// <summary>
    /// 条目仍可用：窗口存在且仍属于登记时的进程。
    /// 窗口关闭后 PID 可能被系统复用，只比较 PID 会把外来窗口误判成本会话应用。
    /// </summary>
    internal static bool IsLiveTrackedApp(TrackedApp app)
    {
        if (app.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(app.Hwnd))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(app.Hwnd, out uint ownerPid);
        return ownerPid == (uint)app.Pid;
    }

    private TrackedApp? LastLiveTrackedApp()
    {
        lock (_gate)
        {
            return _apps.LastOrDefault(IsLiveTrackedApp) ?? TryRelocateLastApp();
        }
    }

    private TrackedApp? FindLiveTrackedApp(int? pid)
    {
        if (pid is null)
        {
            return LastLiveTrackedApp();
        }

        lock (_gate)
        {
            TrackedApp? live = _apps.LastOrDefault(app => app.Pid == pid.Value && IsLiveTrackedApp(app));
            if (live is not null)
            {
                return live;
            }

            TrackedApp? dead = _apps.LastOrDefault(app => app.Pid == pid.Value);
            return dead is not null && TryRelocateApp(dead) ? dead : null;
        }
    }

    /// <summary>
    /// Office 等应用会由启动壳进程交接给新进程，原 pid/hwnd 随即失效。
    /// 按 exe 路径 + 启动时间把死条目重定位到新的顶层窗口，避免成功启动后立即 E_NO_TARGET。
    /// </summary>
    private TrackedApp? TryRelocateLastApp()
    {
        foreach (TrackedApp app in _apps.AsEnumerable().Reverse())
        {
            if (TryRelocateApp(app))
            {
                return app;
            }
        }

        return null;
    }

    private bool TryRelocateApp(TrackedApp app)
    {
        if (string.IsNullOrWhiteSpace(app.ExePath))
        {
            return false;
        }

        WindowInfo? candidate = WindowManager.ListTopLevel()
            .Where(window => !_apps.Any(other => other.Hwnd == window.Hwnd))
            .Where(window => string.Equals(
                WindowManager.GetProcessPath(window.Pid), app.ExePath, StringComparison.OrdinalIgnoreCase))
            .Where(window => ProcessStartedAfter(window.Pid, app.StartedAt))
            .OrderByDescending(window => window.Area)
            .FirstOrDefault();

        if (candidate is null)
        {
            return false;
        }

        _log.Warn(
            $"应用 pid={app.Pid} 已退出，按 exe 重定位到 pid={candidate.Pid} \"{candidate.Title}\"");
        app.Pid = candidate.Pid;
        app.Hwnd = candidate.Hwnd;
        app.Title = candidate.Title;
        app.Placement = WindowManager.GetPlacement(candidate.Hwnd);
        app.PlacementSaved = true;
        return true;
    }

    private static bool ProcessStartedAfter(int pid, DateTime startedAt)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime() >= startedAt.ToUniversalTime().AddSeconds(-2);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按需构造虚拟屏；幂等，必要时自愈。</summary>
    public DisplayInfo EnsureEnvironment()
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ToolException("E_RELEASED", "会话已结束，无法重新建立虚拟屏");
            }

            if (_virtualDisplay is { Attached: true } current &&
                _vdd.IsOpen &&
                GeometryStillValid(current))
            {
                return current;
            }

            string? selfHealReason = null;

            VddDeviceStatus status = VddDevice.QueryStatus();
            if (status != VddDeviceStatus.Ok)
            {
                throw new ToolException("E_DRIVER", $"Parsec VDD 驱动状态异常: {status}");
            }

            if (_vdd.NeedsReopen)
            {
                _log.Warn(
                    $"VDD 心跳连续失败 ({_vdd.ConsecutiveUpdateFailures})，重新打开设备");
                _vdd.Dispose();
                _vddReopenCount++;
                selfHealReason = "vdd_reopen";
            }

            if (!_vdd.TryOpen(out int openError))
            {
                throw new ToolException("E_DRIVER_OPEN", $"打开 VDD 设备失败 (Win32 {openError})");
            }

            _vdd.StartKeepAlive();

            bool configured = false;
            DisplayInfo? existing = DisplayTopology.FindParsecDisplay();

            if (existing is not null && _displayIndex < 0)
            {
                if (_recoveredDisplayIndex >= 0)
                {
                    _log.Warn(
                        $"检测到孤儿虚拟屏，按上次记录接管 " +
                        $"(index={_recoveredDisplayIndex}, device={_recoveredDisplayDevice}, " +
                        $"pid={_recoveredPid})");
                    _displayIndex = _recoveredDisplayIndex;
                    configured = TryActivateDisplay(existing.DeviceName, out string? adoptFailure);

                    if (configured)
                    {
                        _adoptedOrphan = true;
                        selfHealReason = "orphan_adopted";
                        _log.Info($"孤儿虚拟屏已接管: {existing}");
                    }
                    else
                    {
                        _log.Warn($"孤儿虚拟屏接管失败 ({adoptFailure})，尝试重置");
                        TryRemoveDisplay();
                        selfHealReason = "orphan_reclaimed";
                    }
                }
                else
                {
                    // 进程异常退出后驱动通常约 1s 自动回收；给一个短窗口再决定。
                    if (!DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(2)))
                    {
                        throw new ToolException(
                            "E_ORPHAN_DISPLAY",
                            "检测到未归属的虚拟显示器，且无上次会话记录可供安全接管",
                            new JsonObject { ["display"] = existing.ToString() });
                    }

                    _log.Info("孤儿虚拟屏已被驱动自动回收");
                    existing = DisplayTopology.FindParsecDisplay();
                }

                // 一次会话只按上次记录接管一次，避免旧索引在后续重建时被误用。
                _recoveredDisplayIndex = -1;
                _recoveredDisplayDevice = string.Empty;
            }

            // 显示索引仍归本会话，但拓扑丢失/几何被改（睡眠、驱动回收、用户改设置）。
            if (existing is not null && _displayIndex >= 0)
            {
                _log.Warn($"虚拟屏状态异常 ({existing})，尝试恢复");

                if (!configured)
                {
                    configured = TryActivateDisplay(existing.DeviceName, out string? failure);

                    if (!configured)
                    {
                        _log.Warn($"恢复失败 ({failure})，重建虚拟屏");
                        TryRemoveDisplay();
                        _displayRecreateCount++;
                        selfHealReason = "display_recreated";
                    }
                    else if (selfHealReason is null)
                    {
                        selfHealReason = "geometry_changed";
                    }
                }
            }
            else if (existing is null && _displayIndex >= 0)
            {
                // 驱动已回收设备：索引失效，直接丢弃后重新创建，避免对旧索引反复重试。
                _log.Warn("虚拟屏已被驱动回收，重新创建");
                ForgetDisplay();
                _displayRecreateCount++;
                selfHealReason = "display_reclaimed";
            }

            if (!configured && _displayIndex < 0)
            {
                // Parsec 驱动在 REMOVE 后约 3-5 秒内会拒绝 ADD；重试并检查是否已被其他路径创建。
                int[] delays = { 0, 500, 1000, 1500, 2000 };

                foreach (int delay in delays)
                {
                    if (delay > 0)
                    {
                        Thread.Sleep(delay);
                    }

                    _displayIndex = _vdd.AddDisplay();

                    if (_displayIndex >= 0)
                    {
                        break;
                    }

                    if (DisplayTopology.FindParsecDisplay() is not null)
                    {
                        _log.Warn("驱动拒绝 ADD，但检测到已挂载的 Parsec 屏，按已创建处理");
                        break;
                    }

                    _log.Warn("驱动拒绝创建虚拟显示器，准备重试");
                }

                if (_displayIndex < 0 && DisplayTopology.FindParsecDisplay() is null)
                {
                    throw new ToolException(
                        "E_DISPLAY_ADD",
                        "驱动拒绝创建虚拟显示器",
                        retryable: true,
                        hint: "驱动 REMOVE 后需要 3-5 秒冷却；稍后重试或重启驱动",
                        stage: "display");
                }
            }

            if (!configured)
            {
                if (!DisplayTopology.WaitForParsecAdapter(TimeSpan.FromSeconds(3), out DisplayInfo? adapter) ||
                    adapter is null)
                {
                    TryRemoveDisplay();
                    throw new ToolException("E_DISPLAY_ADD", "虚拟显示器未出现在拓扑中");
                }

                configured = TryActivateDisplay(adapter.DeviceName, out string? failure);

                if (!configured)
                {
                    TryRemoveDisplay();
                    throw new ToolException(
                        "E_DISPLAY_MODE",
                        "虚拟显示器模式/坐标未稳定",
                        new JsonObject { ["detail"] = failure });
                }
            }

            DisplayInfo? activated = DisplayTopology.FindParsecDisplay();
            if (activated is null)
            {
                TryRemoveDisplay();
                throw new ToolException("E_DISPLAY_ADD", "虚拟显示器激活后消失");
            }

            _virtualDisplay = activated;
            _initialized = true;

            if (selfHealReason is not null)
            {
                NoteSelfHeal(selfHealReason);
            }

            _cursorGuard?.Dispose();
            _cursorGuard = new CursorGuard(_virtualDisplay, _log);
            _cursorGuard.Start();

            _log.Info($"环境就绪: {_virtualDisplay}");
            SaveState();
            return _virtualDisplay;
        }
    }

    private void NoteSelfHeal(string reason)
    {
        _selfHealed = true;
        _selfHealCount++;
        _lastSelfHealReason = reason;
        _log.Info($"自愈 [{reason}] 累计={_selfHealCount}");
    }

    /// <summary>
    /// 在释放/预热等低负载节点裁剪工作集。不是定时看门狗，只按事件触发。
    /// </summary>
    public JsonObject TrimIdleMemory(string reason)
    {
        long before = 0;
        long afterGc = 0;
        long after = 0;
        bool emptied = false;

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            before = process.WorkingSet64;

            if (before < 36L * 1024 * 1024)
            {
                return new JsonObject
                {
                    ["before_mb"] = Math.Round(before / 1048576.0, 1),
                    ["after_gc_mb"] = Math.Round(before / 1048576.0, 1),
                    ["after_mb"] = Math.Round(before / 1048576.0, 1),
                    ["trimmed"] = false,
                    ["emptied"] = false,
                    ["private_mb"] = Math.Round(process.PrivateMemorySize64 / 1048576.0, 1),
                };
            }

            // 不阻塞等待终结器：UIA COM 释放期强制 GC + WaitForPendingFinalizers
            // 在高频进程启停压测中可能拖住终结器线程，导致进程退出变慢。
            GC.Collect(2, GCCollectionMode.Optimized, blocking: false, compacting: false);

            Thread.Sleep(20);
            process.Refresh();
            afterGc = process.WorkingSet64;

            // GC 后仍高于目标时再清空工作集；否则保留热页，避免下次调用立刻缺页。
            if (afterGc > 40L * 1024 * 1024)
            {
                NativeMethods.SetProcessWorkingSetSize(
                    process.Handle, new IntPtr(-1), new IntPtr(-1));
                Thread.Sleep(50);
                process.Refresh();
                emptied = true;
            }

            after = process.WorkingSet64;
            _log.Info(
                $"内存裁剪 ({reason}): {before / 1048576.0:F1}MB -> " +
                $"gc={afterGc / 1048576.0:F1}MB -> {after / 1048576.0:F1}MB" +
                (emptied ? " (emptied)" : string.Empty));
        }
        catch (Exception exception)
        {
            _log.Warn($"内存裁剪失败 ({reason}): {exception.Message}");
            after = before;
            afterGc = before;
        }

        return new JsonObject
        {
            ["before_mb"] = Math.Round(before / 1048576.0, 1),
            ["after_gc_mb"] = Math.Round(afterGc / 1048576.0, 1),
            ["after_mb"] = Math.Round(after / 1048576.0, 1),
            ["trimmed"] = after > 0 && after < before,
            ["emptied"] = emptied,
        };
    }

    private bool GeometryStillValid(DisplayInfo current)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastGeometryCheckTicks) < 1000)
        {
            return true;
        }

        Interlocked.Exchange(ref _lastGeometryCheckTicks, now);

        DisplayInfo? actual = DisplayTopology.FindParsecDisplay();
        if (actual is null)
        {
            _log.Warn("虚拟屏已从拓扑中消失，准备重建");
            return false;
        }

        (int x, int y) = ExpectedPosition();
        bool matches = actual.Width == VirtualWidth &&
                       actual.Height == VirtualHeight &&
                       actual.X == x &&
                       actual.Y == y;

        if (matches)
        {
            _virtualDisplay = actual;
        }
        else
        {
            _log.Warn($"虚拟屏几何被改变: {actual}，期望 {VirtualWidth}x{VirtualHeight} at ({x},{y})");
        }

        return matches;
    }

    private (int X, int Y) ExpectedPosition()
    {
        DisplayInfo? primary = DisplayTopology.FindPrimary();
        return primary is null
            ? (_virtualDisplay?.X ?? 0, _virtualDisplay?.Y ?? 0)
            : (primary.X + primary.Width, primary.Y);
    }

    private bool TryActivateDisplay(string deviceName, out string? failure)
    {
        failure = null;
        (int x, int y) = ExpectedPosition();

        int setResult = DisplayTopology.SetModeAndPosition(
            deviceName, VirtualWidth, VirtualHeight, VirtualRefresh, x, y, out int applyResult);

        if (!DisplayTopology.WaitUntil(
                () => DisplayTopology.FindParsecDisplay() is { Attached: true },
                TimeSpan.FromSeconds(2)))
        {
            int extendResult = DisplayTopology.ExtendDesktop();
            _log.Warn($"SetDisplayConfig(EXTEND)={extendResult}，重试设置虚拟屏模式");
            setResult = DisplayTopology.SetModeAndPosition(
                deviceName, VirtualWidth, VirtualHeight, VirtualRefresh, x, y, out applyResult);
        }

        bool settled = DisplayTopology.WaitUntil(
            () =>
            {
                DisplayInfo? display = DisplayTopology.FindParsecDisplay();
                return display is not null &&
                       display.Width == VirtualWidth &&
                       display.Height == VirtualHeight &&
                       display.X == x &&
                       display.Y == y;
            },
            TimeSpan.FromSeconds(3));

        if (!settled)
        {
            DisplayInfo? current = DisplayTopology.FindParsecDisplay();
            failure = $"set={setResult} apply={applyResult} current={current?.ToString() ?? "not found"}";
        }

        return settled;
    }

    public JsonObject GetEnvironment(bool ensure = false)
    {
        if (ensure)
        {
            EnsureEnvironment();
        }

        int? focusedPid = null;
        JsonObject result;

        lock (_gate)
        {
            var driver = new JsonObject
            {
                ["status"] = VddDevice.QueryStatus().ToString(),
            };

            if (_vdd.IsOpen)
            {
                int version = _vdd.GetVersion();
                if (version >= 0)
                {
                    driver["version"] = $"{((version >> 16) & 0xFFFF)}.{version & 0xFFFF}";
                }
            }

            int sessionId;

            using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
            {
                sessionId = currentProcess.SessionId;
            }

            var session = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["initialized"] = _initialized,
                ["mutexHost"] = "VScreenPilot_Session_0",
                ["startedAt"] = ProcessStartTime(),
                ["session_id"] = sessionId,
                ["interactive"] = Environment.UserInteractive,
                ["remote_session"] = NativeMethods.GetSystemMetrics(0x1000) != 0,
                ["input_desktop"] = IsInputDesktopAvailable(),
                ["elevated"] = IsElevatedSession(),
                ["isolation"] = "shared_desktop",
            };

            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                session["working_set_mb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1);
                session["handles"] = process.HandleCount;
            }
            catch
            {
                // 忽略。
            }

            var virtualDisplay = new JsonObject();
            DisplayInfo? display = DisplayTopology.FindParsecDisplay();
            if (display is null)
            {
                virtualDisplay["enabled"] = false;
                virtualDisplay["owned"] = false;
                virtualDisplay["self_healed"] = _selfHealed;
                virtualDisplay["self_heal_count"] = _selfHealCount;
                virtualDisplay["last_self_heal_reason"] = _lastSelfHealReason;
                virtualDisplay["adopted_orphan"] = _adoptedOrphan;
                virtualDisplay["display_recreate_count"] = _displayRecreateCount;
                virtualDisplay["vdd_reopen_count"] = _vddReopenCount;
            }
            else
            {
                virtualDisplay["enabled"] = true;
                virtualDisplay["owned"] = _displayIndex >= 0;
                virtualDisplay["device"] = display.DeviceName;
                virtualDisplay["x"] = display.X;
                virtualDisplay["y"] = display.Y;
                virtualDisplay["width"] = display.Width;
                virtualDisplay["height"] = display.Height;
                virtualDisplay["dpi"] = display.Dpi;
                virtualDisplay["scale_percent"] = display.Dpi == 0 ? 0 : (int)Math.Round(display.Dpi / 96.0 * 100);
                virtualDisplay["self_healed"] = _selfHealed;
                virtualDisplay["self_heal_count"] = _selfHealCount;
                virtualDisplay["last_self_heal_reason"] = _lastSelfHealReason;
                virtualDisplay["adopted_orphan"] = _adoptedOrphan;
                virtualDisplay["display_recreate_count"] = _displayRecreateCount;
                virtualDisplay["vdd_reopen_count"] = _vddReopenCount;
                virtualDisplay["lifetime"] = "host_session";
            }

            (int vx, int vy, int vw, int vh) = DisplayTopology.VirtualScreenBounds();
            var virtualScreen = new JsonObject
            {
                ["x"] = vx,
                ["y"] = vy,
                ["width"] = vw,
                ["height"] = vh,
                ["monitors"] = GetMonitorCount(),
            };

            DisplayInfo? primary = DisplayTopology.FindPrimary();
            var primaryJson = primary is null
                ? new JsonObject()
                : new JsonObject
                {
                    ["device"] = primary.DeviceName,
                    ["x"] = primary.X,
                    ["y"] = primary.Y,
                    ["width"] = primary.Width,
                    ["height"] = primary.Height,
                    ["dpi"] = primary.Dpi,
                };

            IntPtr foreground = WindowManager.GetForegroundWindow();
            var foregroundJson = new JsonObject();
            TrackedApp? foregroundApp = null;

            if (foreground != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
                foregroundApp = _apps.FirstOrDefault(
                    app => app.Pid == (int)foregroundPid && IsLiveTrackedApp(app));

                // 只暴露本会话窗口的标题/PID。用户主屏上的窗口标题常含邮件主题、
                // 文档名等隐私信息，不应交给模型。
                foregroundJson["tracked"] = foregroundApp is not null;

                if (foregroundApp is not null)
                {
                    foregroundJson["hwnd"] = foreground.ToInt64();
                    foregroundJson["pid"] = (int)foregroundPid;
                    foregroundJson["title"] = WindowManager.GetTitle(foreground);
                }
            }

            var cursorJson = new JsonObject();
            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                cursorJson["x"] = cursor.X;
                cursorJson["y"] = cursor.Y;
            }

            var apps = new JsonArray();
            foreach (TrackedApp app in _apps)
            {
                WindowInfo? liveWindow = app.Hwnd == IntPtr.Zero
                    ? null
                    : WindowManager.FromHandle(app.Hwnd);

                apps.Add((JsonNode)new JsonObject
                {
                    ["pid"] = app.Pid,
                    ["hwnd"] = app.Hwnd.ToInt64(),
                    ["title"] = liveWindow?.Title ?? app.Title,
                    ["exe"] = app.ExePath,
                    ["alive"] = IsLiveTrackedApp(app),
                    ["launched_by_us"] = app.StartedProcess,
                    ["owned"] = app.Owned,
                    ["close_on_release"] = app.CloseWindowOnRelease,
                    ["dedicated"] = app.DedicatedProcess,
                });
            }

            focusedPid = foregroundApp?.Pid;

            result = new JsonObject
            {
                ["driver"] = driver,
                ["session"] = session,
                ["virtual_display"] = virtualDisplay,
                ["virtual_screen"] = virtualScreen,
                ["primary"] = primaryJson,
                ["foreground"] = foregroundJson,
                ["cursor"] = cursorJson,
                ["apps"] = apps,
            };
        }

        // UIA 读取放到 _gate 外：即使 provider 卡住，release_environment 仍能拿 _gate 清理。
        JsonNode? focusedElement = null;

        if (focusedPid is not null)
        {
            try
            {
                focusedElement = Uia.GetFocusedElement()?.ToJson();
            }
            catch (Exception exception)
            {
                focusedElement = new JsonObject { ["error"] = exception.Message };
            }
        }

        result["focused_element"] = focusedElement;
        return result;
    }

    public JsonObject ReleaseEnvironment(bool keepDisplay, string onDirty = "force")
    {
        lock (_gate)
        {
            var closed = new List<int>();
            var restored = new List<int>();
            var forced = new List<int>();
            var kept = new List<int>();
            var saved = new List<int>();
            var discarded = new List<int>();
            var warnings = new JsonArray();
            bool removed = false;

            onDirty = (onDirty ?? "force").Trim().ToLowerInvariant();

            if (onDirty is not ("force" or "discard" or "save" or "abort"))
            {
                throw new ToolException(
                    "E_INVALID_ARG",
                    $"未知 on_dirty: {onDirty}（可选 force/discard/save/abort）");
            }

            // abort 的语义是“保留未保存窗口”。如果同时摘掉虚拟屏，窗口会被留在
            // 已不存在的显示器坐标上，因此这里强制保留屏，并明确告知调用方。
            bool keepDisplayEffective = keepDisplay;

            if (onDirty == "abort" && !keepDisplayEffective)
            {
                keepDisplayEffective = true;
                warnings.Add("on_dirty=abort 需要保留虚拟屏，已忽略 keep_display=false");
            }

            try
            {
                foreach (TrackedApp app in _apps.AsEnumerable().Reverse().ToList())
                {
                    try
                    {
                        if (app.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(app.Hwnd))
                        {
                            if (app.Owned && app.CloseWindowOnRelease)
                            {
                                string outcome = CloseTrackedApp(app, onDirty, out string? warning);

                                switch (outcome)
                                {
                                    case "closed":
                                        closed.Add(app.Pid);
                                        break;
                                    case "saved":
                                        saved.Add(app.Pid);
                                        closed.Add(app.Pid);
                                        break;
                                    case "discarded":
                                        discarded.Add(app.Pid);
                                        closed.Add(app.Pid);
                                        break;
                                    case "forced":
                                        forced.Add(app.Pid);
                                        closed.Add(app.Pid);
                                        break;
                                    default:
                                        kept.Add(app.Pid);
                                        break;
                                }

                                if (warning is not null)
                                {
                                    warnings.Add(warning);
                                }
                            }
                            else if (app.PlacementSaved)
                            {
                                // 接管（非本会话启动）的窗口只归位、不关闭。
                                WindowManager.SetPlacement(app.Hwnd, app.Placement);
                                restored.Add(app.Pid);
                                kept.Add(app.Pid);
                            }

                            if (app.StartedProcess && app.TempProfile is not null)
                            {
                                KillProcessTreeIfMatches(app.Pid, app.ExePath);
                                forced.Add(app.Pid);
                            }
                        }

                        if (app.TempProfile is not null)
                        {
                            // 窗口已关闭但进程可能仍在后台（Edge 常驻），按 exe 路径核对后再杀，
                            // 避免 PID 复用误杀无关进程。
                            if (app.StartedProcess &&
                                (app.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(app.Hwnd)))
                            {
                                KillProcessTreeIfMatches(app.Pid, app.ExePath);
                            }

                            TryDeleteDirectory(app.TempProfile);
                        }
                        else if (app.CloseWindowOnRelease && app.DedicatedProcess &&
                                 app.StartedProcess &&
                                 (app.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(app.Hwnd)))
                        {
                            // 专用实例的窗口被用户先关掉时，回收可能残留的进程。
                            KillProcessTreeIfMatches(app.Pid, app.ExePath);
                        }
                    }
                    catch (ArgumentException)
                    {
                        if (app.StartedProcess)
                        {
                            closed.Add(app.Pid);
                        }
                    }
                    catch (Exception exception)
                    {
                        _log.Warn($"清理应用 {app.Pid} 失败: {exception.Message}");
                        warnings.Add($"清理 {app.Pid} 失败: {exception.Message}");
                    }
                }
            }
            finally
            {
                // keep_foreground 可能暂停了焦点护栏；释放时恢复，避免用户前台一直留在虚拟屏。
                ResumeFocusGuard();

                _apps.Clear();
                _elements.Clear();
                _elementKeys.Clear();

                // 基线帧是整屏 BGRA（约 3.7MB）；释放后不应继续常驻。
                lock (_frameLock)
                {
                    _frames.Clear();
                }
                _uiaBlockedPids.Clear();
                _nextFrameId = 0;
                _selfHealed = false;

                if (keepDisplayEffective)
                {
                    // 会话级常驻：只归还窗口/关闭本会话应用，不摘屏，避免再次黑屏。
                    _initialized = _virtualDisplay is { Attached: true };
                    SaveState();
                }
                else
                {
                    removed = TryRemoveDisplay();
                    _vdd.StopKeepAlive();
                    _vdd.Dispose();
                    _initialized = false;
                    _stateStore.Clear();
                }

                _log.Info(
                    $"环境已释放 (displayRemoved={removed}, kept={keepDisplayEffective})");
            }

            JsonObject memory = TrimIdleMemory("release");

            var closedSet = closed.ToHashSet();

            return new JsonObject
            {
                ["released"] = true,
                ["display_removed"] = removed,
                ["display_kept"] = keepDisplayEffective,
                ["on_dirty"] = onDirty,
                ["restored_pids"] = ToJsonArray(restored),
                ["closed_pids"] = ToJsonArray(closed),
                ["forced_pids"] = ToJsonArray(forced),
                ["kept_pids"] = ToJsonArray(kept.Where(pid => !closedSet.Contains(pid))),
                ["saved_pids"] = ToJsonArray(saved),
                ["discarded_pids"] = ToJsonArray(discarded),
                ["memory"] = memory,
                ["warnings"] = warnings,
            };
        }
    }

    private static JsonArray ToJsonArray(IEnumerable<int> values)
    {
        var array = new JsonArray();
        foreach (int value in values.Distinct())
        {
            // 避免 JsonArray.Add<T> 在 AOT 下触发 int 的反射元数据。
            array.Add(JsonNode.Parse(value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return array;
    }

    private string CloseTrackedApp(TrackedApp app, string policy, out string? warning)
    {
        warning = null;

        if (policy == "abort")
        {
            return "kept";
        }

        if (policy is "save" or "discard")
        {
            // Office/Chromium 常忽略 WM_CLOSE；直接触发窗口自身的关闭按钮。
            if (!InvokeWindowCloseButton(app.Hwnd))
            {
                NativeMethods.PostMessageW(app.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
            }

            WaitForWindowClosed(app.Hwnd, 500);

            if (NativeMethods.IsWindow(app.Hwnd))
            {
                bool save = policy == "save";
                bool dialogHandled;

                // 保存框也走 UIA；如果 provider 卡死，不能让 release 永久挂住。
                Task<bool> dialogTask = Task.Run(
                    () => TryHandleSaveDialog(app.Pid, app.Hwnd, save, 1500));

                if (dialogTask.Wait(3000))
                {
                    dialogHandled = dialogTask.Result;
                }
                else
                {
                    ResetUia();
                    dialogHandled = false;
                    warning = "保存对话框处理超时，已放弃 UIA 并继续清理";
                }

                if (dialogHandled)
                {
                    WaitForWindowClosed(app.Hwnd, 3000);

                    if (!NativeMethods.IsWindow(app.Hwnd))
                    {
                        WaitForProcessExit(app.Pid, 1000);
                        return save ? "saved" : "discarded";
                    }

                    warning = $"{policy} 对话框已处理，但窗口仍未关闭";
                }
                else
                {
                    warning = "未找到保存对话框，将按后续超时策略处理";
                }
            }
        }
        else
        {
            NativeMethods.PostMessageW(app.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }

        if (!WaitForWindowClosed(app.Hwnd, 2500) && NativeMethods.IsWindow(app.Hwnd))
        {
            if (app.DedicatedProcess ||
                (app.StartedProcess && !HasOtherWindows(app.Pid, app.Hwnd)))
            {
                KillProcessTreeIfMatches(app.Pid, app.ExePath);
                WaitForProcessExit(app.Pid, 2000);
                return "forced";
            }

            warning = $"窗口未能关闭，保留共享进程 {app.Pid}";
            return "kept";
        }

        if (app.DedicatedProcess && IsProcessAlive(app.Pid))
        {
            // 窗口已关闭但进程常驻（Office/Edge 常见）；保持原有兜底。
            KillProcessTree(app.Pid);
            return "forced";
        }

        return "closed";
    }

    private bool TryHandleSaveDialog(int pid, IntPtr mainHwnd, bool save, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
        var seen = new List<string>();

        while (DateTime.UtcNow < deadline)
        {
            List<WindowInfo> candidates = WindowManager.ListTopLevel()
                .Where(item => item.Pid == pid && item.Hwnd != mainHwnd)
                .ToList();

            foreach (WindowInfo candidate in candidates)
            {
                if (!seen.Contains(candidate.Title))
                {
                    seen.Add(candidate.Title);
                }

                if (InvokeDialogButton(candidate.Hwnd, save))
                {
                    _log.Info($"已处理保存对话框: {candidate.Title}");
                    return true;
                }
            }

            // Office 的保存提示常常是主窗口内的子对话框。先确认存在“不保存”，
            // 再点“保存/不保存”，避免误点功能区里的保存按钮。
            if (HasDialogButton(mainHwnd) && InvokeDialogButton(mainHwnd, save))
            {
                _log.Info($"已处理主窗口内的保存提示 ({(save ? "save" : "discard")})");
                return true;
            }

            Thread.Sleep(120);
        }

        _log.Warn($"未找到保存对话框；候选窗口: {string.Join(" | ", seen)}");
        return false;
    }

    private bool HasDialogButton(IntPtr hwnd)
    {
        try
        {
            return Uia.FindElements(
                hwnd, "不保存", UiaNameMatch.Exact, "Button", null, 5).Count > 0 ||
                   Uia.FindElements(
                hwnd, "Don't Save", UiaNameMatch.Exact, "Button", null, 5).Count > 0;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private bool InvokeWindowCloseButton(IntPtr hwnd)
    {
        try
        {
            foreach (string name in new[] { "关闭", "Close" })
            {
                List<UiaElementInfo> buttons = Uia.FindElements(
                    hwnd, name, UiaNameMatch.Exact, "Button", null, 10);

                UiaElementInfo? target = buttons
                    .Where(item => item.Enabled && item.Width > 0 && item.Height > 0)
                    .OrderByDescending(item => item.Left)
                    .FirstOrDefault();

                if (target is null || string.IsNullOrEmpty(target.RuntimeId))
                {
                    continue;
                }

                using UiaElementHandle? handle = Uia.FindByRuntimeId(hwnd, target.RuntimeId);

                if (handle is not null)
                {
                    (bool ok, _) = handle.Invoke();
                    if (ok)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or ArgumentException)
        {
            // 继续走 WM_CLOSE/超时策略。
        }

        return false;
    }

    private bool InvokeDialogButton(IntPtr dialogHwnd, bool save)
    {
        try
        {
            List<UiaElementInfo> buttons = Uia.FindElements(
                dialogHwnd, null, UiaNameMatch.Contains, "Button", null, 30);

            UiaElementInfo? target = buttons.FirstOrDefault(item =>
            {
                string name = item.Name ?? string.Empty;

                if (save)
                {
                    return !string.Equals(item.AutomationId, "FileSave", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("不保存", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("Don't Save", StringComparison.OrdinalIgnoreCase) &&
                           (name.Contains("保存", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Save", StringComparison.OrdinalIgnoreCase));
                }

                return name.Contains("不保存", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("不儲存", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Don't Save", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("否", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("放弃", StringComparison.OrdinalIgnoreCase);
            });

            if (target is null || string.IsNullOrEmpty(target.RuntimeId))
            {
                return false;
            }

            using UiaElementHandle? handle = Uia.FindByRuntimeId(dialogHwnd, target.RuntimeId);

            if (handle is null)
            {
                return false;
            }

            (bool ok, _) = Uia.TryInvoke(handle.Element);
            return ok;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static void WaitForProcessExit(int pid, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));

        while (DateTime.UtcNow < deadline && IsProcessAlive(pid))
        {
            Thread.Sleep(50);
        }
    }

    private static bool WaitForWindowClosed(IntPtr hwnd, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !NativeMethods.IsWindow(hwnd);
    }

    private static bool HasOtherWindows(int pid, IntPtr exclude)
    {
        try
        {
            return WindowManager.ListTopLevel().Any(
                w => w.Pid == pid && w.Hwnd != exclude && !string.IsNullOrWhiteSpace(w.Title));
        }
        catch
        {
            return false;
        }
    }

    private static void KillProcessTree(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已退出。
        }
    }

    /// <summary>仅在 PID 仍对应记录的 exe 时才杀进程树，避免 PID 复用误杀。</summary>
    private static void KillProcessTreeIfMatches(int pid, string expectedPath)
    {
        if (string.IsNullOrEmpty(expectedPath))
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return;
            }

            string? modulePath = process.MainModule?.FileName;
            if (modulePath is null ||
                !string.Equals(
                    Path.GetFullPath(modulePath),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已退出或无法读取模块路径。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Edge 可能仍持有锁，留给下次启动清理。
        }
    }

    private void ForgetDisplay()
    {
        _displayIndex = -1;
        _virtualDisplay = null;
    }

    private bool TryRemoveDisplay()
    {
        _cursorGuard?.Dispose();
        _cursorGuard = null;

        bool removed = false;

        if (_displayIndex >= 0 && _vdd.IsOpen)
        {
            removed = _vdd.RemoveDisplay(_displayIndex);
            if (!removed)
            {
                _log.Warn($"摘除虚拟屏失败 (index={_displayIndex})，等待驱动回收");
            }

            DisplayTopology.WaitForNoParsecDisplay(TimeSpan.FromSeconds(3));
        }

        _displayIndex = -1;
        _virtualDisplay = null;
        return removed;
    }

    public void SaveState()
    {
        lock (_gate)
        {
            var state = new SessionState
            {
                Pid = Environment.ProcessId,
                StartedAt = ProcessStartTime(),
                DisplayIndex = _displayIndex,
                DisplayDevice = _virtualDisplay?.DeviceName ?? string.Empty,
                X = _virtualDisplay?.X ?? 0,
                Y = _virtualDisplay?.Y ?? 0,
                Width = _virtualDisplay?.Width ?? 0,
                Height = _virtualDisplay?.Height ?? 0,
            };

            foreach (TrackedApp app in _apps)
            {
                state.Apps.Add(new AppState
                {
                    Pid = app.Pid,
                    ExePath = app.ExePath,
                    Title = app.Title,
                    Hwnd = app.Hwnd.ToInt64(),
                    LaunchedByUs = app.StartedProcess,
                    ShowCmd = app.Placement.showCmd,
                    MinX = app.Placement.minPosition.X,
                    MinY = app.Placement.minPosition.Y,
                    MaxX = app.Placement.maxPosition.X,
                    MaxY = app.Placement.maxPosition.Y,
                    Left = app.Placement.normalPosition.Left,
                    Top = app.Placement.normalPosition.Top,
                    Right = app.Placement.normalPosition.Right,
                    Bottom = app.Placement.normalPosition.Bottom,
                });
            }

            _stateStore.Save(state);
        }
    }

    private void RecoverFromPreviousCrash()
    {
        SessionState? state = _stateStore.Load();
        if (state is null || state.Pid == Environment.ProcessId)
        {
            return;
        }

        _recoveredPid = state.Pid;
        _recoveredDisplayIndex = state.DisplayIndex;
        _recoveredDisplayDevice = state.DisplayDevice;

        if (state.DisplayIndex >= 0)
        {
            _log.Info(
                $"发现上次会话状态: pid={state.Pid}, displayIndex={state.DisplayIndex}, " +
                $"device={state.DisplayDevice}, apps={state.Apps.Count}");
        }

        // 互斥体保证不会有两个 VScreenPilot；若旧 pid 仍对应同名进程，
        // 说明它还没退干净，此时不冒险接管它的屏。
        if (state.Pid > 0 && IsVScreenPilotProcess(state.Pid))
        {
            _log.Warn($"上次会话进程仍在运行 (pid={state.Pid})，跳过孤儿屏接管");
            _recoveredDisplayIndex = -1;
            _recoveredDisplayDevice = string.Empty;
        }

        foreach (AppState app in state.Apps)
        {
            var hwnd = new IntPtr(app.Hwnd);
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            // 句柄可能已被系统复用：必须确认仍属于记录中的进程。
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
            if (ownerPid != (uint)app.Pid)
            {
                _log.Warn($"崩溃恢复跳过已复用的窗口句柄 0x{app.Hwnd:X} (pid={app.Pid})");
                continue;
            }

            var placement = new NativeMethods.WINDOWPLACEMENT
            {
                length = Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
                flags = 0,
                showCmd = app.ShowCmd,
                minPosition = new NativeMethods.POINT { X = app.MinX, Y = app.MinY },
                maxPosition = new NativeMethods.POINT { X = app.MaxX, Y = app.MaxY },
                normalPosition = new NativeMethods.RECT
                {
                    Left = app.Left,
                    Top = app.Top,
                    Right = app.Right,
                    Bottom = app.Bottom,
                },
            };

            if (WindowManager.SetPlacement(hwnd, placement))
            {
                _log.Info($"崩溃恢复: 已还原窗口 {app.Title} (pid={app.Pid})");
            }
        }

        _stateStore.Clear();
    }

    /// <summary>
    /// 可选的启动白名单：VSP_APP_ALLOWLIST 用分号分隔目录。
    /// 未配置时保持原有“允许任意 exe”的行为；配置后只允许白名单目录下的完整路径。
    /// </summary>
    private static string[] ReadAppAllowlist()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_APP_ALLOWLIST");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var roots = new List<string>();

        foreach (string part in raw.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                roots.Add(Path.GetFullPath(part).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                // 忽略无效路径。
            }
        }

        return roots.ToArray();
    }

    private void EnsureAppAllowed(string? path)
    {
        if (_appAllowlist.Length == 0 || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException("E_INVALID_ARG", $"应用路径无效: {exception.Message}");
        }

        if (_appAllowlist.Any(root => IsUnder(fullPath, root)))
        {
            return;
        }

        var allowedRoots = new JsonArray();
        foreach (string root in _appAllowlist)
        {
            allowedRoots.Add(root);
        }

        throw new ToolException(
            "E_APP_NOT_ALLOWED",
            "该应用不在 VSP_APP_ALLOWLIST 白名单内，拒绝启动",
            new JsonObject
            {
                ["path"] = fullPath,
                ["allowed_roots"] = allowedRoots,
            });
    }

    private static string ProcessStartTime()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.StartTime.ToString("O");
        }
        catch
        {
            return DateTimeOffset.Now.ToString("O");
        }
    }

    private static bool IsVScreenPilotProcess(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(process.ProcessName, "VScreenPilot", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsInputDesktopAvailable()
    {
        IntPtr desktop = NativeMethods.OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.CloseDesktop(desktop);
        return true;
    }

    private static bool IsElevatedSession()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static int GetMonitorCount()
    {
        return NativeMethods.GetSystemMetrics(80);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        StopFocusGuard();

        try
        {
            ReleaseEnvironment(keepDisplay: false);
        }
        catch (Exception exception)
        {
            _log.Error("释放环境失败", exception);
        }

        _job.Dispose();
        _vdd.Dispose();
        _stateStore.Clear();
    }
}
