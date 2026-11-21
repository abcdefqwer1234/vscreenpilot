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

    /// <summary>本会话的客户端登记处；每个 SessionManager 一份，不使用进程级静态。</summary>
    internal ClientSessionRegistry Clients { get; } = new();

    private readonly ClientPathPolicy _paths;

    private readonly SessionGuard _guard = new();
    private readonly LogRing _log;
    private readonly StateStore _stateStore;
    private readonly JobObject _job;
    private readonly TrackedAppRegistry _apps;
    private readonly bool _allowScript;
    private readonly string[] _appAllowlist;

    private int _disposed;

    private readonly FrameStore _frames = new();
    // 按区域保留更多基线，避免只截另一区域就把当前区域基线挤掉。
    private const int MaxFrames = 8;
    private readonly Dictionary<string, HashSet<int>> _uiaBlockedPids =
        new(StringComparer.Ordinal);

    private readonly UiaHost _uiaHost = new();
    private readonly VirtualDisplayController _display;
    private readonly AppReleaser _releaser;
    private readonly AppLauncher _launcher;
    private CursorGuard? _cursorGuard;
    // 每调用一个异步流：并发工具不会互相清掉取消令牌（旧实现是单个字段）。
    private readonly AsyncLocal<CancellationTokenSource?> _cancellation = new();
    private long _epoch;

    internal bool DisplayPositionFallback
    {
        get => _display.State.PositionFallback;
        set => _display.State.PositionFallback = value;
    }

    private volatile bool _focusGuardPaused;
    private volatile bool _focusGuardRunning;
    private readonly object _focusActionGate = new();
    private Thread? _focusGuardThread;
    private IntPtr _lastUserForeground;
    private long _nextFocusRestoreTicks;
    private long _focusGuardResumeAtTicks;

    private void OnDisplayReady(DisplayInfo display)
    {
        _cursorGuard?.Dispose();
        _cursorGuard = new CursorGuard(display, _log.Info, _log.Warn);
        _cursorGuard.Start();
    }

    private void OnDisplayRemoved()
    {
        _cursorGuard?.Dispose();
        _cursorGuard = null;
    }

    private UiaHost Uia => _uiaHost;

    /// <summary>
    /// 放弃当前 UIA 代际并换新实例。只在 UIA 调用超时/疑似卡死时调用：
    /// 旧代际的工作线程会把手上的 COM 调用做完再自行释放，不在本线程 Dispose。
    /// </summary>
    public void ResetUia()
    {
        if (_uiaHost.Retire())
        {
            _log.Warn("UIA 服务疑似卡死，已切换到新代际（旧实例后台收尾释放）");
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
    /// <summary>
    /// 把当前异步流的取消令牌固化成回调：UIA 宿主在自己的 MTA 线程上调用它，
    /// AsyncLocal 不会跨手工线程流动，必须在调用方先取令牌。
    /// </summary>
    private Func<bool> CancellationProbe()
    {
        CancellationToken token = Cancellation;
        return () => token.IsCancellationRequested;
    }


    /// <summary>会话代际：release_environment 之后递增，旧 handler 的结果会被丢弃。</summary>
    public long Epoch => Clients.Current is { } client
        ? Interlocked.Read(ref client.Epoch)
        : Interlocked.Read(ref _epoch);

    private void IncrementEpoch()
    {
        if (Clients.Current is { } client)
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
            string clientId = Clients.CurrentId;

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

        _paths = new ClientPathPolicy(Clients);
        _apps = new TrackedAppRegistry(_gate);
        _snapshots = new SnapshotBuilder(this);
        _environment = new EnvironmentReporter(this);
        _display = new VirtualDisplayController(
            _gate, log, ProfileName,
            () => Volatile.Read(ref _disposed) != 0,
            OnDisplayReady,
            OnDisplayRemoved,
            SaveState,
            VirtualWidth,
            VirtualHeight,
            VirtualRefresh);
        _releaser = new AppReleaser(log, _uiaHost, ResetUia);
        _launcher = new AppLauncher(this);
        _log.ClientIdProvider = () => Clients.CurrentId;
        AppLauncher.CleanupStaleProfiles();
        SweepOldArtifacts();
        RecoverFromPreviousCrash();
        StartMaintenance();
    }

    public LogRing Log => _log;

    public bool AllowScript => _allowScript;

    public bool Initialized => _display.State.Initialized;

    public DisplayInfo? VirtualDisplay => _display.State.VirtualDisplay;

    public IReadOnlyList<TrackedApp> Apps => _apps.Snapshot();

    /// <summary>控制管道遍历 apps 时使用的稳定快照，避免与 launch/release 并发修改冲突。</summary>
    internal IReadOnlyList<TrackedApp> GetAppsSnapshot()
    {
        lock (_gate)
        {
            return _apps.Snapshot();
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
        foreach (TrackedApp app in _apps.Snapshot().AsEnumerable().Reverse().Where(IsOwned))
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
    public DisplayInfo EnsureEnvironmentFast() => _display.EnsureFast();

    public DisplayInfo EnsureEnvironment() => _display.Ensure();

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

    public EnvironmentView GetEnvironment(bool ensure = false) =>
        _environment.GetEnvironment(ensure);

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

            if (!keepDisplayEffective && Clients.ActiveCount > 1 && !forceRemoveDisplay)
            {
                keepDisplayEffective = true;
                warnings.Add("其他客户端仍在使用虚拟屏，已忽略 keep_display=false");
            }

            try
            {
                IEnumerable<TrackedApp> releaseApps = forceAllClients
                    ? _apps.Snapshot()
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
                                AppReleaser.KillProcessTreeIfMatches(app.Pid, app.ExePath);
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
                                AppReleaser.KillProcessTreeIfMatches(app.Pid, app.ExePath);
                            }

                            AppReleaser.TryDeleteDirectory(app.TempProfile);
                        }
                        else if (app.CloseWindowOnRelease && app.DedicatedProcess &&
                                 app.StartedProcess &&
                                 (app.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(app.Hwnd)))
                        {
                            // 专用实例的窗口被用户先关掉时，回收可能残留的进程。
                            AppReleaser.KillProcessTreeIfMatches(app.Pid, app.ExePath);
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
                string releaseClient = Clients.CurrentId;

                if (forceAllClients)
                {
                    _refs.ClearUnsafe();
                }
                else
                {
                    _refs.DropClientUnsafe(releaseClient);
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
                    _display.State.SelfHealed = false;
                }
                else
                {
                    _uiaBlockedPids.Remove(releaseClient);
                }

                if (keepDisplayEffective)
                {
                    // 会话级常驻：只归还窗口/关闭本会话应用，不摘屏，避免再次黑屏。
                    _display.State.Initialized = _display.State.VirtualDisplay is { Attached: true };
                    SaveState();
                }
                else
                {
                    removed = TryRemoveDisplay();
                    _display.Vdd.StopKeepAlive();
                    _display.Vdd.Dispose();
                    _display.State.Initialized = false;
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

    private string CloseTrackedApp(TrackedApp app, string policy, out string? warning) =>
        _releaser.Close(app, policy, out warning);

    private void ForgetDisplay() => _display.Forget();

    private DisplayInfo? OwnDisplay() => _display.OwnDisplay();

    private bool TryRemoveDisplay() => _display.TryRemove();

    public void SaveState()
    {
        lock (_gate)
        {
            var state = new SessionState
            {
                Pid = Environment.ProcessId,
                StartedAt = ProcessStartTime(),
                DisplayIndex = _display.State.Index,
                DisplayDevice = _display.State.VirtualDisplay?.DeviceName ?? _display.State.DeviceName,
                X = _display.State.VirtualDisplay?.X ?? 0,
                Y = _display.State.VirtualDisplay?.Y ?? 0,
                Width = _display.State.VirtualDisplay?.Width ?? 0,
                Height = _display.State.VirtualDisplay?.Height ?? 0,
            };

            foreach (TrackedApp app in _apps.Snapshot())
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

        _display.State.RecoveredPid = state.Pid;
        _display.State.RecoveredIndex = state.DisplayIndex;
        _display.State.RecoveredDevice = state.DisplayDevice;

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
            _display.State.RecoveredIndex = -1;
            _display.State.RecoveredDevice = string.Empty;
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
            catch (Exception exception)
            {
                // 忽略无效路径。
                _ = exception;
            }
        }

        return roots.ToArray();
    }

    private void EnsureAppAllowed(string? path)
    {
        string[] clientAllowlist = Clients.Current?.AppAllowlist ?? Array.Empty<string>();
        bool hasClientRule = Clients.Current is not null && clientAllowlist.Length > 0;
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
            fullPath = _paths.Resolve(path);
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

    /// <summary>路径是否位于根目录下（大小写不敏感，供应用白名单使用）。</summary>
    private static bool IsUnder(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
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
                if (_display.State.Index >= 0 &&
                    !string.IsNullOrWhiteSpace(_display.State.DeviceName) &&
                    DisplayTopology.FindParsecDisplayByDevice(_display.State.DeviceName) is not null)
                {
                    _display.Vdd.RemoveDisplay(_display.State.Index);
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
            _uiaHost.Dispose();
            _snapshots.Dispose();
        }
        catch (Exception exception)
        {
            // 退出路径上的释放失败不影响进程结束。
            _ = exception;
        }

        _job.Dispose();
        _display.Vdd.Dispose();
        _stateStore.Clear();
    }
}
