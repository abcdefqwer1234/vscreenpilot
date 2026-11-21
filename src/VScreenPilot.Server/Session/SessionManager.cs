using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Diagnostics;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>
/// 会话生命周期：互斥体、按需挂载虚拟屏、窗口归位、统一清理。
/// </summary>
internal sealed partial class SessionManager : IDisposable
{
    private static readonly Options.VspOptions HostOptions = Options.VspOptions.FromEnvironment();

    public static readonly int VirtualWidth = HostOptions.DisplayWidth;
    public static readonly int VirtualHeight = HostOptions.DisplayHeight;
    public static readonly int VirtualRefresh = HostOptions.DisplayRefresh;

    private readonly object _gate = new();

    private readonly SessionGuard _guard = new();
    private readonly LogRing _log;
    private readonly StateStore _stateStore;
    private readonly JobObject _job;
    private readonly VddDevice _vdd = new();
    private readonly List<TrackedApp> _apps = new();
    private readonly bool _allowScript;
    private readonly string[] _appAllowlist;

    private DisplayInfo? _virtualDisplay;
    private string _displayDevice = string.Empty;
    private int _displayIndex = -1;
    private bool _initialized;
    private int _disposed;

    private readonly FrameStore _frames = new();
    // 按区域保留更多基线，避免只截另一区域就把当前区域基线挤掉。
    private const int MaxFrames = 8;
    private readonly Dictionary<string, HashSet<int>> _uiaBlockedPids =
        new(StringComparer.Ordinal);
    private bool _jobUnavailableLogged;

    private UiaService? _uia;
    private readonly List<UiaService> _retiredUia = new();
    private CursorGuard? _cursorGuard;
    // 每调用一个异步流：并发工具不会互相清掉取消令牌（旧实现是单个字段）。
    private readonly AsyncLocal<CancellationTokenSource?> _cancellation = new();
    private long _epoch;
    private long _lastGeometryCheckTicks;
    private bool _selfHealed;
    private int _selfHealCount;
    private string? _lastSelfHealReason;
    private bool _adoptedOrphan;
    private bool _displayPositionFallback;

    internal bool DisplayPositionFallback
    {
        get => _displayPositionFallback;
        set => _displayPositionFallback = value;
    }
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

    private UiaService Uia
    {
        get
        {
            UiaService? current = _uia;

            if (current is not null)
            {
                return current;
            }

            var created = new UiaService();
            UiaService? existing = Interlocked.CompareExchange(ref _uia, created, null);
            return existing ?? created;
        }
    }

    /// <summary>
    /// 放弃当前 UIA 服务并换新实例。只在 UIA 调用超时/疑似卡死时调用：
    /// 旧实例可能仍有线程卡在 COM 调用里，不能 Dispose，只能让新调用走新实例。
    /// </summary>
    public void ResetUia()
    {
        UiaService? old = Interlocked.Exchange(ref _uia, null);

        if (old is null)
        {
            return;
        }

        _log.Warn("UIA 服务疑似卡死，已切换到新实例（旧实例后台释放）");

        // 不在 Reset 时立刻 Dispose：卡死实例可能仍有线程在 COM 调用里，
        // 从另一线程释放 RCW 有 use-after-free 风险。只做有界退休，会话结束时统一释放。
        lock (_retiredUia)
        {
            _retiredUia.Add(old);

            // 不能在这里 Dispose：退休实例可能仍有 COM 调用在飞（Reset 通常由超时触发），
            // 立刻释放会造成 use-after-free。改为全部保留到会话 Dispose 时统一释放；
            // 之前“超过 4 个直接丢弃”会让这些 RCW 只能等终结器，长会话下持续泄漏。
            if (_retiredUia.Count > 8)
            {
                _log.Warn($"UIA 退休实例已累积 {_retiredUia.Count} 个，将在会话结束时统一释放");
            }
        }
    }

    /// <summary>当前工具调用的取消令牌（由 MCP 层设置，供 wait_for 等长操作观察）。</summary>
    public CancellationToken Cancellation
    {
        get
        {
            CancellationTokenSource? source = _cancellation.Value;
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

    public void SetCancellation(CancellationTokenSource? source) => _cancellation.Value = source;

    /// <summary>会话代际：release_environment 之后递增，旧 handler 的结果会被丢弃。</summary>
    public long Epoch => CurrentClient is { } client
        ? Interlocked.Read(ref client.Epoch)
        : Interlocked.Read(ref _epoch);

    private void IncrementEpoch()
    {
        if (CurrentClient is { } client)
        {
            Interlocked.Increment(ref client.Epoch);
        }
        else
        {
            Interlocked.Increment(ref _epoch);
        }
    }

    public void EnterRead() => _guard.EnterRead();

    public void ExitRead() => _guard.ExitRead();

    public bool TryEnterExclusive(int milliseconds) =>
        _guard.TryEnterExclusive(milliseconds);

    public void ExitExclusive() => _guard.ExitExclusive();

    /// <summary>多会话 profile 名（--profile / VSP_PROFILE）；默认 default。</summary>
    public string ProfileName { get; } = Profile.Resolve();

    /// <summary>进程内唯一的写调度器（daemon 内所有连接共享）。</summary>
    internal WriteScheduler Scheduler { get; } = new();

    /// <summary>进程内唯一的屏幕租约（daemon 内所有连接共享）。</summary>
    internal ScreenLease Lease { get; } = new();

    private readonly object _artifactGate = new();
    private readonly Dictionary<string, string> _artifactDirectories = new(StringComparer.Ordinal);
    private ArtifactStore? _artifacts;

    /// <summary>大截图/快照的落盘目录；按客户端隔离，通过 vsp://artifacts/ 资源 URI 暴露。</summary>
    public string ArtifactDirectory
    {
        get
        {
            string clientId = CurrentClientId;

            lock (_artifactGate)
            {
                if (_artifactDirectories.TryGetValue(clientId, out string? cached))
                {
                    return cached;
                }

                string created = CreateArtifactDirectory(clientId);
                _artifactDirectories[clientId] = created;
                return created;
            }
        }
    }

    /// <summary>截图/快照的 profile 根目录；按会话再建时间戳子目录。</summary>
    public string ArtifactRoot => ArtifactRootFor(ProfileName);

    public static string ArtifactRootFor(string profile) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VScreenPilot",
        "artifacts",
        profile);

    private string CreateArtifactDirectory(string clientId)
    {
        string root = Path.Combine(
            ArtifactRoot,
            clientId,
            $"{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}");
        Directory.CreateDirectory(root);
        return root;
    }

    public SessionManager(
        LogRing log, StateStore stateStore, JobObject job, bool allowScript = false)
    {
        _log = log;
        _stateStore = stateStore;
        _job = job;
        _allowScript = allowScript;
        _appAllowlist = ReadAppAllowlist();
        _log.ClientIdProvider = static () => CurrentClientId;
        CleanupStaleProfiles();
        SweepOldArtifacts();
        RecoverFromPreviousCrash();
        StartMaintenance();
    }

    public LogRing Log => _log;

    public bool AllowScript => _allowScript;

    public bool Initialized => _initialized;

    public DisplayInfo? VirtualDisplay => _virtualDisplay;

    public IReadOnlyList<TrackedApp> Apps => _apps;

    /// <summary>控制管道遍历 apps 时使用的稳定快照，避免与 launch/release 并发修改冲突。</summary>
    internal IReadOnlyList<TrackedApp> GetAppsSnapshot()
    {
        lock (_gate)
        {
            return _apps.ToArray();
        }
    }

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

        _ = NativeMethods.GetWindowThreadProcessId(app.Hwnd, out uint ownerPid);
        return ownerPid == (uint)app.Pid;
    }

    private TrackedApp? LastLiveTrackedApp()
    {
        lock (_gate)
        {
            return _apps.LastOrDefault(app => IsOwned(app) && IsLiveTrackedApp(app))
                   ?? TryRelocateLastApp();
        }
    }

    /// <summary>清理已死超过 3s 的条目，避免 apps[] 长期残留（N5）。</summary>
    private void PruneDeadApps()
    {
        lock (_gate)
        {
            DateTime now = DateTime.UtcNow;

            for (int index = _apps.Count - 1; index >= 0; index--)
            {
                TrackedApp app = _apps[index];

                if (IsLiveTrackedApp(app))
                {
                    app.DeadSince = null;
                    continue;
                }

                app.DeadSince ??= now;

                if ((now - app.DeadSince.Value).TotalSeconds >= 3)
                {
                    _apps.RemoveAt(index);
                }
            }
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
            TrackedApp? live = _apps.LastOrDefault(
                app => app.Pid == pid.Value && IsOwned(app) && IsLiveTrackedApp(app));
            if (live is not null)
            {
                return live;
            }

            TrackedApp? dead = _apps.LastOrDefault(app => app.Pid == pid.Value && IsOwned(app));
            return dead is not null && TryRelocateApp(dead) ? dead : null;
        }
    }

    /// <summary>
    /// Office 等应用会由启动壳进程交接给新进程，原 pid/hwnd 随即失效。
    /// 按 exe 路径 + 启动时间把死条目重定位到新的顶层窗口，避免成功启动后立即 E_NO_TARGET。
    /// </summary>
    private TrackedApp? TryRelocateLastApp()
    {
        foreach (TrackedApp app in _apps.AsEnumerable().Reverse().Where(IsOwned))
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
        if (!IsOwned(app) || string.IsNullOrWhiteSpace(app.ExePath))
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
            $"应用 pid={app.Pid} 已退出，按 exe 重定位到 pid={candidate.Pid}");
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

    /// <summary>
    /// 读路径的快速入口：已挂载就直接返回，不拿 _gate，也不重算拓扑。
    /// 真正失效时再回落到 EnsureEnvironment 自愈。
    /// </summary>
    public DisplayInfo EnsureEnvironmentFast()
    {
        DisplayInfo? display = _virtualDisplay;

        return display is { Attached: true } && _vdd.IsOpen
            ? display
            : EnsureEnvironment();
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
            bool displayAdded = false;
            string? targetDevice = null;
            DisplayInfo? existing = OwnDisplay();

            if (existing is null)
            {
                DisplayInfo? attached = DisplayTopology.FindParsecDisplay();

                // 已经有其他 live profile 认领的屏不能接管；否则会抢别人的屏。
                if (attached is not null && !IsDisplayOwnedByOther(attached.DeviceName))
                {
                    bool matchesRecovered = !string.IsNullOrWhiteSpace(_recoveredDisplayDevice) &&
                                            string.Equals(
                                                attached.DeviceName,
                                                _recoveredDisplayDevice,
                                                StringComparison.OrdinalIgnoreCase);

                    // 不认领“来源不明”的屏；只有自己的崩溃 state 匹配才允许。
                    // 另一个 profile 正在启动（registry 已存在但 display_device 还没写）时，
                    // 不要抢它的屏，也不要直接失败，直接申请一块新的。
                    if (matchesRecovered)
                    {
                        existing = attached;
                    }
                    else if (HasOtherLiveProfile())
                    {
                        existing = null;
                    }
                    else if (!string.Equals(ProfileName, "default", StringComparison.Ordinal))
                    {
                        throw new ToolException(
                            "E_ORPHAN_DISPLAY",
                            "检测到未归属的虚拟显示器；非默认 profile 不自动接管，避免抢其他 profile 的屏",
                            new JsonObject
                            {
                                ["display"] = attached.ToString(),
                                ["profile"] = ProfileName,
                            },
                            retryable: true,
                            hint: "确认该屏无人使用后手动摘除，或使用默认 profile/独立 VM",
                            stage: "display");
                    }
                    else
                    {
                        existing = attached;
                    }
                }
            }

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
                        _displayDevice = existing.DeviceName;
                        _adoptedOrphan = true;
                        selfHealReason = "orphan_adopted";
                        _log.Info($"孤儿虚拟屏已接管: {existing}");
                    }
                    else
                    {
                        _log.Warn($"孤儿虚拟屏接管失败 ({adoptFailure})，尝试重置");
                        RemoveDisplayAfterEnsureFailure(displayAdded);
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

            if (existing is not null)
            {
                targetDevice = existing.DeviceName;
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
                        RemoveDisplayAfterEnsureFailure(displayAdded);
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
                // ADD 与“解析新屏 device”必须整体串行：只锁 ADD 时，两个 profile 可能在
                // 解析阶段把同一块刚挂上的屏都认成自己的（后写 registry 的会覆盖前者）。
                Mutex? allocation = TryAcquireDisplayAllocation(out bool allocationHeld);

                try
                {
                    // Parsec 驱动在 REMOVE 后约 3-5 秒内会拒绝 ADD；重试并等待本次新变可用的屏。
                    // 不能用“设备名是否出现过”判断：驱动会复用已有 detached 适配器的名字（本机 DISPLAY5~20）。
                    HashSet<string> beforeLive = DisplayTopology.LiveParsecDevices();

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
                            displayAdded = true;
                            break;
                        }

                        if (DisplayTopology.PickNewLiveParsec(
                                DisplayTopology.List(), beforeLive, IsDisplayOwnedByOther) is not null)
                        {
                            _log.Warn("驱动拒绝 ADD，但检测到本次新变可用的 Parsec 屏，按已创建处理");
                            break;
                        }

                        _log.Warn("驱动拒绝创建虚拟显示器，准备重试");
                    }

                    if (_displayIndex < 0 &&
                        DisplayTopology.PickNewLiveParsec(
                            DisplayTopology.List(), beforeLive, IsDisplayOwnedByOther) is null)
                    {
                        throw new ToolException(
                            "E_DISPLAY_ADD",
                            "驱动拒绝创建虚拟显示器",
                            new JsonObject
                            {
                                ["before_live"] = DescribeDevices(beforeLive),
                                ["after_live"] = DescribeDevices(DisplayTopology.LiveParsecDevices()),
                            },
                            retryable: true,
                            hint: "驱动 REMOVE 后需要 3-5 秒冷却；稍后重试或重启驱动",
                            stage: "display");
                    }

                    if (string.IsNullOrWhiteSpace(targetDevice))
                    {
                        if (!DisplayTopology.WaitForNewLiveParsecDisplay(
                                beforeLive,
                                TimeSpan.FromSeconds(3),
                                out DisplayInfo? added,
                                IsDisplayOwnedByOther) ||
                            added is null)
                        {
                            JsonObject details = new()
                            {
                                ["display_index"] = _displayIndex,
                                ["before_live"] = DescribeDevices(beforeLive),
                                ["after_live"] = DescribeDevices(DisplayTopology.LiveParsecDevices()),
                            };

                            _log.Warn($"新建虚拟屏未转可用: {details.ToJsonString()}");
                            RemoveDisplayAfterEnsureFailure(displayAdded);
                            throw new ToolException(
                                "E_DISPLAY_ADD",
                                "新建的虚拟显示器未出现在拓扑中",
                                details,
                                retryable: true,
                                hint: "确认 Parsec VDD 正常；detached 适配器不算已存在，若仍失败请提供 details",
                                stage: "display");
                        }

                        targetDevice = added.DeviceName;
                        _log.Info($"新建虚拟屏已可用: {added}");
                    }
                }
                finally
                {
                    ReleaseDisplayAllocation(allocation, allocationHeld);
                }
            }

            if (!configured)
            {
                if (string.IsNullOrWhiteSpace(targetDevice))
                {
                    RemoveDisplayAfterEnsureFailure(displayAdded);
                    throw new ToolException("E_DISPLAY_ADD", "无法确定本次虚拟屏的 device");
                }

                configured = TryActivateDisplay(targetDevice, out string? failure);

                if (!configured)
                {
                    RemoveDisplayAfterEnsureFailure(displayAdded);
                    throw new ToolException(
                        "E_DISPLAY_MODE",
                        "虚拟显示器模式/坐标未稳定",
                        new JsonObject { ["detail"] = failure });
                }
            }

            DisplayInfo? activated = DisplayTopology.FindParsecDisplayByDevice(targetDevice);
            if (activated is null)
            {
                RemoveDisplayAfterEnsureFailure(displayAdded);
                throw new ToolException("E_DISPLAY_ADD", "虚拟显示器激活后消失");
            }

            _virtualDisplay = activated;
            _displayDevice = activated.DeviceName;
            _initialized = true;

            if (selfHealReason is not null)
            {
                NoteSelfHeal(selfHealReason);
            }

            _cursorGuard?.Dispose();
            _cursorGuard = new CursorGuard(_virtualDisplay, _log.Info, _log.Warn);
            _cursorGuard.Start();

            _log.Info($"环境就绪: {_virtualDisplay}");
            SaveState();
            return _virtualDisplay;
        }
    }

    /// <summary>
    /// 不同 profile 同时启动时串行化“ADD + 解析 device”，避免抢同一个 slot 或同一块新屏。
    /// 拿不到锁时返回 null（降级为不串行）；设备身份校验仍会防止认领错屏。
    /// </summary>
    private static Mutex? TryAcquireDisplayAllocation(out bool held)
    {
        held = false;

        try
        {
            var allocation = new Mutex(false, @"Local\VScreenPilot_DisplayAlloc");

            try
            {
                held = allocation.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException)
            {
                held = true;
            }

            return allocation;
        }
        catch
        {
            return null;
        }
    }

    private static void ReleaseDisplayAllocation(Mutex? allocation, bool held)
    {
        if (allocation is null)
        {
            return;
        }

        try
        {
            if (held)
            {
                allocation.ReleaseMutex();
            }
        }
        catch
        {
            // 释放失败不应覆盖原始错误。
        }
        finally
        {
            allocation.Dispose();
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
    public MemoryView TrimIdleMemory(string reason)
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
                return new MemoryView(
                    Math.Round(before / 1048576.0, 1),
                    Math.Round(before / 1048576.0, 1),
                    Math.Round(before / 1048576.0, 1),
                    Trimmed: false,
                    Emptied: false,
                    PrivateMb: Math.Round(process.PrivateMemorySize64 / 1048576.0, 1));
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

        return new MemoryView(
            Math.Round(before / 1048576.0, 1),
            Math.Round(afterGc / 1048576.0, 1),
            Math.Round(after / 1048576.0, 1),
            Trimmed: after > 0 && after < before,
            Emptied: emptied);
    }

    private bool GeometryStillValid(DisplayInfo current)
    {
        long now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastGeometryCheckTicks) < 1000)
        {
            return true;
        }

        Interlocked.Exchange(ref _lastGeometryCheckTicks, now);

        DisplayInfo? actual = OwnDisplay();
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

        if (primary is null)
        {
            return (_virtualDisplay?.X ?? 0, _virtualDisplay?.Y ?? 0);
        }

        // 多 profile 不能都放在主屏右侧同一点：默认 profile 偏移 0，
        // 其他 profile 用稳定哈希取 1..7 个屏宽，避免互相覆盖。
        int offset = DisplayOffset();

        return (primary.X + primary.Width + offset, primary.Y);
    }

    private int DisplayOffset()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_DISPLAY_OFFSET");

        if (int.TryParse(raw, out int configured))
        {
            return Math.Clamp(configured, 0, 20000);
        }

        // 选择“第一个真正空闲的槽位”，避免第 8 个 profile 与前面的重叠。
        // 计算时排除本会话自己的 Parsec 屏，否则每次启动都会把上次的位置当成占用而漂移。
        DisplayInfo? own = OwnDisplay();
        var occupied = new HashSet<int>();

        foreach (DisplayInfo display in DisplayTopology.List())
        {
            if (own is not null &&
                string.Equals(display.DeviceName, own.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            occupied.Add(display.X);
        }

        DisplayInfo? primary = DisplayTopology.FindPrimary();
        int originX = primary is null ? 0 : primary.X + primary.Width;
        int preferredSlot = 0;

        if (!string.Equals(ProfileName, "default", StringComparison.Ordinal))
        {
            int hash = 17;

            foreach (char ch in ProfileName)
            {
                hash = unchecked(hash * 31 + ch) & 0x7FFFFFFF;
            }

            preferredSlot = hash % 8;
        }

        for (int step = 0; step < 8; step++)
        {
            int slot = (preferredSlot + step) % 8;

            if (!occupied.Contains(originX + slot * VirtualWidth))
            {
                return slot * VirtualWidth;
            }
        }

        return preferredSlot * VirtualWidth;
    }

    private bool TryActivateDisplay(string deviceName, out string? failure)
    {
        failure = null;
        DisplayPositionFallback = false;
        (int x, int y) = ExpectedPosition();

        int setResult = DisplayTopology.SetModeAndPosition(
            deviceName, VirtualWidth, VirtualHeight, VirtualRefresh, x, y, out int applyResult);

        if (!DisplayTopology.WaitUntil(
                () => DisplayTopology.FindParsecDisplayByDevice(deviceName) is { Attached: true },
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
                DisplayInfo? display = DisplayTopology.FindParsecDisplayByDevice(deviceName);
                return display is not null &&
                       display.Width == VirtualWidth &&
                       display.Height == VirtualHeight &&
                       display.X == x &&
                       display.Y == y;
            },
            TimeSpan.FromSeconds(3));

        if (!settled)
        {
            DisplayInfo? current = DisplayTopology.FindParsecDisplayByDevice(deviceName);

            // Windows/VDD 可能忽略 dmPosition，把新屏固定在主屏右侧。
            // 只要分辨率正确且已挂载，就接受实际坐标（否则整个 profile 直接不可用）。
            if (current is { Attached: true } &&
                current.Width == VirtualWidth &&
                current.Height == VirtualHeight)
            {
                DisplayPositionFallback = true;
                failure =
                    $"position_not_applied expected=({x},{y}) actual=({current.X},{current.Y})";
                _log.Warn($"虚拟屏位置未生效，接受实际坐标 ({current.X},{current.Y})");
                return true;
            }

            failure = $"set={setResult} apply={applyResult} current={current?.ToString() ?? "not found"}";
        }

        return settled;
    }

    public EnvironmentView GetEnvironment(bool ensure = false)
    {
        if (ensure)
        {
            EnsureEnvironment();
        }

        PruneDeadApps();

        int? focusedPid = null;
        EnvironmentView? view = null;

        // 锁外做可能慢的驱动/显示枚举：launch/release 持 _gate 时 get_environment 不应被拖住。
        string driverStatus = VddDevice.QueryStatus().ToString();
        DisplayInfo? parsecDisplay = OwnDisplay();
        (int vx, int vy, int vw, int vh) = DisplayTopology.VirtualScreenBounds();
        DisplayInfo? primaryDisplay = DisplayTopology.FindPrimary();
        int monitorCount = GetMonitorCount();
        IntPtr foregroundWindow = WindowManager.GetForegroundWindow();

        lock (_gate)
        {
            string? driverVersion = null;

            if (_vdd.IsOpen)
            {
                int version = _vdd.GetVersion();
                if (version >= 0)
                {
                    driverVersion = $"{((version >> 16) & 0xFFFF)}.{version & 0xFFFF}";
                }
            }

            int sessionId;

            using (var currentProcess = System.Diagnostics.Process.GetCurrentProcess())
            {
                sessionId = currentProcess.SessionId;
            }

            var clientRoots = new List<string>();

            foreach (string root in CurrentRoots)
            {
                if (!string.IsNullOrWhiteSpace(root))
                {
                    clientRoots.Add(root);
                }
            }

            JsonArray clientList = ClientSummariesJson();
            int otherClients = clientList.Count;

            if (CurrentClient is null || !CurrentClient.IncludeAllApps)
            {
                otherClients = Math.Max(0, otherClients - 1);
            }

            int? proxyPid = null;
            string? clientVersion = null;

            if (CurrentClient is { ProxyPid: > 0 } currentClient)
            {
                proxyPid = currentClient.ProxyPid;

                if (!string.IsNullOrWhiteSpace(currentClient.Version))
                {
                    clientVersion = currentClient.Version;
                }
            }

            double? workingSetMb = null;
            int? handles = null;

            try
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                workingSetMb = Math.Round(process.WorkingSet64 / 1048576.0, 1);
                handles = process.HandleCount;
            }
            catch
            {
                // 进程句柄被回收时忽略；字段保持缺省，不影响诊断的其他部分。
            }

            var session = new SessionView
            {
                Pid = Environment.ProcessId,
                Initialized = _initialized,
                MutexHost = Profile.MutexName(ProfileName),
                StartedAt = ProcessStartTime(),
                SessionId = sessionId,
                Interactive = Environment.UserInteractive,
                RemoteSession = NativeMethods.GetSystemMetrics(0x1000) != 0,
                InputDesktop = IsInputDesktopAvailable(),
                Elevated = Elevation.IsProcessElevated(),
                Isolation = "shared_desktop",
                Profile = ProfileName,
                Epoch = Epoch,
                JobObject = _job.Status,
                Role = "daemon",
                DaemonPid = Environment.ProcessId,
                ClientId = CurrentClientId,
                ClientCwd = CurrentCwd,
                ClientRoots = clientRoots,
                ProxyPid = proxyPid,
                ClientVersion = clientVersion,
                Clients = clientList,
                OtherClients = otherClients,
                ScreenLease = ScreenLeaseProvider?.Invoke(),
                WorkingSetMb = workingSetMb,
                Handles = handles,
            };

            DisplayInfo? display = parsecDisplay;
            VirtualDisplayView virtualDisplay;

            if (display is null)
            {
                virtualDisplay = new VirtualDisplayView
                {
                    Enabled = false,
                    Owned = false,
                    SelfHealed = _selfHealed,
                    SelfHealCount = _selfHealCount,
                    LastSelfHealReason = _lastSelfHealReason,
                    AdoptedOrphan = _adoptedOrphan,
                    DisplayRecreateCount = _displayRecreateCount,
                    VddReopenCount = _vddReopenCount,
                    PositionEnforced = true,
                };
            }
            else
            {
                virtualDisplay = new VirtualDisplayView
                {
                    Enabled = true,
                    Owned = _displayIndex >= 0 &&
                            !string.IsNullOrWhiteSpace(_displayDevice) &&
                            string.Equals(display.DeviceName, _displayDevice, StringComparison.OrdinalIgnoreCase),
                    Device = display.DeviceName,
                    X = display.X,
                    Y = display.Y,
                    Width = display.Width,
                    Height = display.Height,
                    Dpi = (int)display.Dpi,
                    ScalePercent = display.Dpi == 0 ? 0 : (int)Math.Round(display.Dpi / 96.0 * 100),
                    SelfHealed = _selfHealed,
                    SelfHealCount = _selfHealCount,
                    LastSelfHealReason = _lastSelfHealReason,
                    AdoptedOrphan = _adoptedOrphan,
                    DisplayRecreateCount = _displayRecreateCount,
                    VddReopenCount = _vddReopenCount,
                    PositionEnforced = !_displayPositionFallback,
                    Lifetime = "host_session",
                };
            }

            var virtualScreen = new VirtualScreenView
            {
                X = vx,
                Y = vy,
                Width = vw,
                Height = vh,
                Monitors = monitorCount,
            };

            DisplayInfo? primary = primaryDisplay;
            var primaryView = primary is null
                ? new PrimaryView()
                : new PrimaryView
                {
                    Device = primary.DeviceName,
                    X = primary.X,
                    Y = primary.Y,
                    Width = primary.Width,
                    Height = primary.Height,
                    Dpi = (int)primary.Dpi,
                };

            IntPtr foreground = foregroundWindow;
            var foregroundView = new ForegroundView();
            TrackedApp? foregroundApp = null;

            if (foreground != IntPtr.Zero)
            {
                _ = NativeMethods.GetWindowThreadProcessId(foreground, out uint foregroundPid);
                foregroundApp = _apps.FirstOrDefault(
                    app => IsOwned(app) && app.Pid == (int)foregroundPid && IsLiveTrackedApp(app));

                // 只暴露本会话窗口的标题/PID。用户主屏上的窗口标题常含邮件主题、
                // 文档名等隐私信息，不应交给模型。
                foregroundView = new ForegroundView
                {
                    Tracked = foregroundApp is not null,
                    Hwnd = foregroundApp is null ? null : foreground.ToInt64(),
                    Pid = foregroundApp is null ? null : (int)foregroundPid,
                    Title = foregroundApp is null ? null : WindowManager.GetTitle(foreground),
                };
            }

            var cursorView = new CursorView();

            if (NativeMethods.GetCursorPos(out NativeMethods.POINT cursor))
            {
                cursorView = new CursorView { X = cursor.X, Y = cursor.Y };
            }

            var apps = new List<AppView>();
            bool includeOwner = CurrentClient?.IncludeAllApps == true;

            foreach (TrackedApp app in _apps.Where(IsOwned))
            {
                WindowInfo? liveWindow = app.Hwnd == IntPtr.Zero
                    ? null
                    : WindowManager.FromHandle(app.Hwnd);

                apps.Add(new AppView
                {
                    Pid = app.Pid,
                    Hwnd = app.Hwnd.ToInt64(),
                    Title = liveWindow?.Title ?? app.Title,
                    Exe = app.ExePath,
                    Alive = IsLiveTrackedApp(app),
                    LaunchedByUs = app.StartedProcess,
                    Owned = app.Owned,
                    CloseOnRelease = app.CloseWindowOnRelease,
                    Dedicated = app.DedicatedProcess,
                    ClientId = includeOwner ? app.OwnerClientId : null,
                });
            }

            focusedPid = foregroundApp?.Pid;

            view = new EnvironmentView
            {
                Driver = new DriverView { Status = driverStatus, Version = driverVersion },
                Session = session,
                VirtualDisplay = virtualDisplay,
                VirtualScreen = virtualScreen,
                Primary = primaryView,
                Foreground = foregroundView,
                Cursor = cursorView,
                Apps = apps,
                Metrics = MetricsRegistry.ToView(),
            };
        }

        // UIA 读取放到 _gate 外：即使 provider 卡住，release_environment 仍能拿 _gate 清理。
        JsonNode? focusedElement = null;

        if (focusedPid is not null)
        {
            try
            {
                focusedElement = Uia.GetFocusedElement()?.ToJson();

                // 二次校验：焦点元素必须属于当前客户端（含 UWP 内容进程）。
                if (focusedElement is JsonObject focus &&
                    focus["process_id"] is JsonValue pidValue &&
                    pidValue.TryGetValue(out int focusPid))
                {
                    bool owned;

                    lock (_gate)
                    {
                        owned = _apps.Any(app =>
                            IsOwned(app) &&
                            (app.Pid == focusPid || app.ContentPid == focusPid));
                    }

                    if (!owned)
                    {
                        focusedElement = null;
                    }
                }
            }
            catch (Exception exception)
            {
                focusedElement = new JsonObject { ["error"] = exception.Message };
            }
        }

        int focusRedactions = 0;
        PrivacyRedactor.RedactJsonStrings(focusedElement, ref focusRedactions);

        return view! with
        {
            PrivacyRedactions = focusRedactions > 0 ? focusRedactions : null,
            FocusedElement = focusedElement,
        };
    }

    public ReleaseReport ReleaseEnvironment(
        bool keepDisplay, string onDirty = "abort", bool allowDataLoss = false,
        bool forceRemoveDisplay = false, bool forceAllClients = false)
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

            onDirty = (onDirty ?? "abort").Trim().ToLowerInvariant();

            if (onDirty is not ("force" or "discard" or "save" or "abort"))
            {
                throw new ToolException(
                    "E_INVALID_ARG",
                    $"未知 on_dirty: {onDirty}（可选 force/discard/save/abort）");
            }

            if (onDirty is "force" or "discard" && !allowDataLoss)
            {
                throw new ToolException(
                    "E_DATA_LOSS_REQUIRED",
                    $"on_dirty={onDirty} 会丢弃未保存内容，需要显式 allow_data_loss=true",
                    new JsonObject { ["on_dirty"] = onDirty },
                    retryable: true,
                    hint: "先保存；确认可丢弃后传 allow_data_loss=true，或改用 on_dirty=save/abort",
                    stage: "release");
            }

            // 校验通过后再推进代际：一次被拒绝的 release 不应让在途结果作废。
            IncrementEpoch();

            // abort 的语义是“保留未保存窗口”。如果同时摘掉虚拟屏，窗口会被留在
            // 已不存在的显示器坐标上，因此这里强制保留屏，并明确告知调用方。
            bool keepDisplayEffective = keepDisplay;

            if (onDirty == "abort" && !keepDisplayEffective)
            {
                keepDisplayEffective = true;
                warnings.Add("on_dirty=abort 需要保留虚拟屏，已忽略 keep_display=false");
            }

            if (!keepDisplayEffective && ActiveClientCount > 1 && !forceRemoveDisplay)
            {
                keepDisplayEffective = true;
                warnings.Add("其他客户端仍在使用虚拟屏，已忽略 keep_display=false");
            }

            try
            {
                IEnumerable<TrackedApp> releaseApps = forceAllClients
                    ? _apps
                    : _apps.Where(IsOwned);

                foreach (TrackedApp app in releaseApps.Reverse().ToList())
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

                // abort 的语义是“保留未保存窗口”，因此必须保留跟踪信息：
                // 否则后续 release 无法关闭/归位这些窗口，虚拟屏上会留下工具看不见的孤儿。
                if (onDirty != "abort")
                {
                    _apps.RemoveAll(app => forceAllClients || IsOwned(app));
                }

                // 注意：这里已持 _gate；不要再拿 _refGate（Ref 路径的锁顺序是 _refGate -> _gate）。
                // MCP 层用 session 读写锁保证 release 不与 find/tree 并发。
                string releaseClient = CurrentClientId;

                if (forceAllClients)
                {
                    _elements.Clear();
                    _elementKeys.Clear();
                    ClearSnapshotState(clientId: null);
                }
                else
                {
                    foreach ((string refKey, ElementRefEntry entry) in _elements
                                 .Where(pair => string.Equals(
                                     pair.Value.OwnerClientId, releaseClient, StringComparison.Ordinal))
                                 .ToList())
                    {
                        _elements.Remove(refKey);
                        _elementKeys.Remove(entry.Key);
                    }

                    ClearSnapshotState(releaseClient);
                }

                // 基线帧是整屏 BGRA（约 3.7MB）；只清当前客户端的帧，避免 A release 清掉 B 的基线。
                                if (forceAllClients)
                {
                    _frames.Clear();
                }
                else
                {
                    _frames.RemoveClient(releaseClient);
                }
            
                if (forceAllClients)
                {
                    _uiaBlockedPids.Clear();
                    _selfHealed = false;
                }
                else
                {
                    _uiaBlockedPids.Remove(releaseClient);
                }

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

            MemoryView memory = TrimIdleMemory("release");

            var closedSet = closed.ToHashSet();

            return new ReleaseReport(
                Released: true,
                DisplayRemoved: removed,
                DisplayKept: keepDisplayEffective,
                OnDirty: onDirty,
                RestoredPids: restored.Distinct().ToArray(),
                ClosedPids: closed.Distinct().ToArray(),
                ForcedPids: forced.Distinct().ToArray(),
                KeptPids: kept.Where(pid => !closedSet.Contains(pid)).Distinct().ToArray(),
                SavedPids: saved.Distinct().ToArray(),
                DiscardedPids: discarded.Distinct().ToArray(),
                Memory: memory,
                Warnings: warnings.Select(item => item!.GetValue<string>()).ToArray());
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

        // UWP 的 Pid/ExePath 是共享的 ApplicationFrameHost；
        // 必须按内容进程关闭/杀，绝不能强杀 frame host。
        bool uwp = app.ContentPid > 0;
        int closePid = uwp ? app.ContentPid : app.Pid;
        string closePath = uwp ? app.ContentExePath : app.ExePath;

        if (policy == "abort")
        {
            return "kept";
        }

        if (policy is "save" or "discard")
        {
            // 先走标准 WM_CLOSE：它等价于用户点标题栏 X，不会误点文档/标签页的关闭按钮。
            // 只有窗口忽略 WM_CLOSE 时，才退化为 UIA 查找关闭按钮。
            NativeMethods.PostMessageW(app.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

            if (!WaitForWindowClosed(app.Hwnd, 500))
            {
                InvokeWindowCloseButton(app.Hwnd);
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
                if (uwp && string.IsNullOrEmpty(closePath))
                {
                    warning = "UWP 内容进程路径不可用，拒绝强杀共享 frame host";
                    return "kept";
                }

                KillProcessTreeIfMatches(closePid, closePath);
                WaitForProcessExit(closePid, 2000);
                return "forced";
            }

            warning = $"窗口未能关闭，保留共享进程 {app.Pid}";
            return "kept";
        }

        if (app.DedicatedProcess && IsProcessAlive(closePid))
        {
            // 窗口已关闭但进程常驻（Office/Edge 常见）；保持原有兜底。
            KillProcessTreeIfMatches(closePid, closePath);
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
                    _log.Info($"已处理保存对话框: class={candidate.ClassName}");
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

            // 先按 Win32 对话框按钮 ID（是/否/取消 = 6/7/2）识别，避免只支持中英文；
            // 文本匹配只作为兜底。
            string[] preferredIds = save
                ? new[] { "6", "1" }
                : new[] { "7", "2" };

            UiaElementInfo? target = buttons.FirstOrDefault(
                item => preferredIds.Contains(item.AutomationId, StringComparer.OrdinalIgnoreCase));

            target ??= buttons.FirstOrDefault(item =>
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
    private static readonly HashSet<string> ProtectedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "explorer", "dwm", "sihost", "shellexperiencehost",
        "startmenuexperiencehost", "textinputhost", "searchhost", "taskhostw", "runtimebroker",
    };

    private static void KillProcessTreeIfMatches(int pid, string expectedPath)
    {
        if (string.IsNullOrEmpty(expectedPath))
        {
            return;
        }

        if (ProtectedSystemProcesses.Contains(Path.GetFileNameWithoutExtension(expectedPath)))
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
        _displayDevice = string.Empty;
        _virtualDisplay = null;
    }

    private DisplayInfo? OwnDisplay()
    {
        DisplayInfo? byDevice = DisplayTopology.FindParsecDisplayByDevice(_displayDevice);

        if (byDevice is not null)
        {
            return byDevice;
        }

        return _virtualDisplay is { Attached: true }
            ? DisplayTopology.FindParsecDisplayByDevice(_virtualDisplay.DeviceName)
            : null;
    }

    private static bool HasOtherLiveProfile() =>
        InstanceRegistry.ListAll().Any(entry => entry.Pid != Environment.ProcessId);

    private static bool IsDisplayOwnedByOther(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName))
        {
            return false;
        }

        return InstanceRegistry.ListAll()
            .Concat(InstanceRegistry.ListStateEntries())
            .Any(entry =>
                entry.Pid != Environment.ProcessId &&
                string.Equals(entry.DisplayDevice, deviceName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>把设备名转成稳定排序的 JSON 数组，便于日志和错误 details。</summary>
    private static JsonArray DescribeDevices(IEnumerable<string> devices)
    {
        var array = new JsonArray();
        foreach (string device in devices.OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            array.Add(device);
        }

        return array;
    }

    /// <summary>
    /// EnsureEnvironment 失败时：若是本次刚 ADD 的屏，index 一定属于本会话，可直接回收；
    /// 否则走带 device 校验的 TryRemoveDisplay，避免误摘其他 profile。
    /// </summary>
    private void RemoveDisplayAfterEnsureFailure(bool displayAdded)
    {
        if (displayAdded && _displayIndex >= 0)
        {
            try
            {
                _vdd.RemoveDisplay(_displayIndex);
            }
            catch (Exception exception)
            {
                _log.Warn($"回收本次新建虚拟屏失败: {exception.Message}");
            }

            _displayIndex = -1;
            _displayDevice = string.Empty;
            _virtualDisplay = null;
            return;
        }

        TryRemoveDisplay();
    }

    private bool TryRemoveDisplay()
    {
        _cursorGuard?.Dispose();
        _cursorGuard = null;

        bool removed = false;
        string device = _displayDevice;

        if (_displayIndex >= 0 && _vdd.IsOpen)
        {
            // index 可能被驱动复用；摘除前必须确认它仍对应本会话的 device。
            DisplayInfo? own = string.IsNullOrWhiteSpace(device)
                ? null
                : DisplayTopology.FindParsecDisplayByDevice(device);

            if (string.IsNullOrWhiteSpace(device))
            {
                // 没有 device 身份时不能按 index 猜；否则可能摘掉其他 profile 的屏。
                _log.Warn($"没有本会话 device 记录，跳过按 index={_displayIndex} 摘除");
            }
            else if (own is null)
            {
                _log.Warn($"虚拟屏 {device} 已不在拓扑中，跳过按 index 摘除");
            }
            else
            {
                removed = _vdd.RemoveDisplay(_displayIndex);
                if (!removed)
                {
                    _log.Warn($"摘除虚拟屏失败 (index={_displayIndex})，等待驱动回收");
                }

                DisplayTopology.WaitForDisplayGone(device, TimeSpan.FromSeconds(3));
            }
        }

        _displayIndex = -1;
        _displayDevice = string.Empty;
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
                DisplayDevice = _virtualDisplay?.DeviceName ?? _displayDevice,
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

        // 互斥体保证不会有两个 VScreenPilot；若旧 pid 仍对应同名且启动时间匹配的进程，
        // 说明它还没退干净，此时不冒险接管它的屏，也不恢复/清空 state。
        if (state.Pid > 0 && IsVScreenPilotProcess(state.Pid, state.StartedAt))
        {
            _log.Warn($"上次会话进程仍在运行 (pid={state.Pid})，跳过全部恢复");
            _recoveredDisplayIndex = -1;
            _recoveredDisplayDevice = string.Empty;
            return;
        }

        foreach (AppState app in state.Apps)
        {
            var hwnd = new IntPtr(app.Hwnd);
            if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            {
                continue;
            }

            // 句柄可能已被系统复用：必须确认仍属于记录中的进程。
            _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
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
                _log.Info($"崩溃恢复: 已还原窗口 (pid={app.Pid})");
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
        string[] clientAllowlist = CurrentClient?.AppAllowlist ?? Array.Empty<string>();
        bool hasClientRule = CurrentClient is not null && clientAllowlist.Length > 0;
        bool hasGlobalRule = _appAllowlist.Length > 0;

        if (!hasClientRule && !hasGlobalRule)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;

        try
        {
            fullPath = ResolvePath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException("E_INVALID_ARG", $"应用路径无效: {exception.Message}");
        }

        bool clientOk = !hasClientRule || clientAllowlist.Any(root => IsUnder(fullPath, root));
        bool globalOk = !hasGlobalRule || _appAllowlist.Any(root => IsUnder(fullPath, root));

        if (clientOk && globalOk)
        {
            return;
        }

        // 两层规则取交集：客户端自己的白名单只能收紧 daemon 的全局白名单，不能绕过它。
        string[] failedRoots = !clientOk ? clientAllowlist : _appAllowlist;
        var allowedRoots = new JsonArray();
        foreach (string root in failedRoots)
        {
            allowedRoots.Add(root);
        }

        throw new ToolException(
            "E_APP_NOT_ALLOWED",
            "该应用不在 VSP_APP_ALLOWLIST 白名单内，拒绝启动",
            new JsonObject
            {
                ["path"] = fullPath,
                ["scope"] = !clientOk ? "client" : "global",
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

    private static bool IsVScreenPilotProcess(int pid, string startedAt)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            bool nameMatches = string.Equals(
                process.ProcessName, "VScreenPilot", StringComparison.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(startedAt) ||
                !DateTimeOffset.TryParse(startedAt, out DateTimeOffset expected))
            {
                return nameMatches;
            }

            // 以启动时间为准：dotnet 运行、改名 exe 也能识别；避免 PID 复用误判。
            return Math.Abs(
                (process.StartTime.ToUniversalTime() - expected.UtcDateTime).TotalSeconds) <= 5;
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

        _maintenanceTimer?.Dispose();
        _maintenanceTimer = null;
        StopFocusGuard();

        // 清理不能无限等：如果某个 handler 卡在 UIA COM 里并持有 _gate，
        // 直接 Dispose 会让宿主退出永久挂起。给 3s 预算，超时就做最小清理。
        Task cleanup = Task.Run(() =>
        {
            try
            {
                // 进程退出必须摘屏并回收本会话进程；不能沿用 abort 的“保留窗口/保留屏”语义，
                // 否则会留下孤儿虚拟屏（下次启动可能 E_ORPHAN_DISPLAY）。
                ReleaseEnvironment(
                    keepDisplay: false,
                    onDirty: "force",
                    allowDataLoss: true,
                    forceRemoveDisplay: true,
                    forceAllClients: true);
            }
            catch (Exception exception)
            {
                _log.Error("释放环境失败", exception);
            }
        });

        if (!cleanup.Wait(TimeSpan.FromSeconds(3)))
        {
            _log.Warn("清理超过 3s 未完成，执行最小清理并退出");

            try
            {
                // 超时兜底也只能摘“自己的 device”，不能按可能被复用的 index 盲摘。
                if (_displayIndex >= 0 &&
                    !string.IsNullOrWhiteSpace(_displayDevice) &&
                    DisplayTopology.FindParsecDisplayByDevice(_displayDevice) is not null)
                {
                    _vdd.RemoveDisplay(_displayIndex);
                }
            }
            catch (Exception exception)
            {
                _log.Warn($"强制摘屏失败: {exception.Message}");
            }
        }

        // 会话结束时统一释放 UIA COM 引用（当前 + 退休实例）。
        try
        {
            Interlocked.Exchange(ref _uia, null)?.Dispose();

            lock (_retiredUia)
            {
                foreach (UiaService retired in _retiredUia)
                {
                    retired.Dispose();
                }

                _retiredUia.Clear();
            }
        }
        catch
        {
            // 退出路径上的释放失败不影响进程结束。
        }

        _job.Dispose();
        _vdd.Dispose();
        _stateStore.Clear();
    }
}
