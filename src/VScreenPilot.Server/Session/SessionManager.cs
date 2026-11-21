using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
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
internal sealed partial class SessionManager : IDisposable, ISessionStateHost, IAppAllowlistHost, IEnvironmentReleaserHost, IMaintenanceHost, IElementFinderHost, IRegionVerifierHost, IWaitHost, IElementResolverHost, IElementOperationsHost, IScreenTextHost, IScreenCaptureHost, IFocusHost, ITrackedAppHost
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
    private readonly AppAllowlistPolicy _allowlist;
    private readonly EnvironmentReleaser _releaserService;

    private int _disposed;

    private readonly FrameStore _frames = new();
    // 按区域保留更多基线，避免只截另一区域就把当前区域基线挤掉。
    private const int MaxFrames = 8;

    private readonly UiaHost _uiaHost = new();
    private readonly VirtualDisplayController _display;
    private readonly AppReleaser _releaser;
    private readonly AppLauncher _launcher;
    private readonly InputRouter _input;
    private readonly ElementFinder _finder;
    private readonly TrackedAppTracker _tracker;
    private readonly SessionStateKeeper _stateKeeper;
    private readonly RegionVerifier _regionVerifier;
    private readonly WaitService _waits;
    private readonly ElementResolver _resolver;
    private readonly ElementOperations _elements;
    private readonly ScreenTextService _screenText;
    private readonly ScreenCaptureService _capture;
    private CursorGuard? _cursorGuard;
    // 每调用一个异步流：并发工具不会互相清掉取消令牌（旧实现是单个字段）。
    private readonly AsyncLocal<CancellationTokenSource?> _cancellation = new();
    private long _epoch;

    internal bool DisplayPositionFallback
    {
        get => _display.State.PositionFallback;
        set => _display.State.PositionFallback = value;
    }



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
        _input = new InputRouter(this);
        _visual = new VisualMatchService(this);
        _finder = new ElementFinder(this);
        _regionVerifier = new RegionVerifier(this);
        _waits = new WaitService(this);
        _resolver = new ElementResolver(this);
        _elements = new ElementOperations(this);
        _screenText = new ScreenTextService(this);
        _capture = new ScreenCaptureService(this);
        _focus = new FocusGuard(this);
        _tracker = new TrackedAppTracker(this);
        _stateKeeper = new SessionStateKeeper(this);
        _allowlist = new AppAllowlistPolicy(this);
        _releaserService = new EnvironmentReleaser(this);
        _maintenance = new MaintenanceService(this);
        _log.ClientIdProvider = () => Clients.CurrentId;
        AppLauncher.CleanupStaleProfiles();
        SweepOldArtifacts();
        RecoverFromPreviousCrash();
        StartMaintenance();
    }

    public LogRing Log => _log;

    internal MetricsRegistry Metrics { get; } = new();

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
    internal static bool IsLiveTrackedApp(TrackedApp app) => TrackedAppTracker.IsLive(app);

    private TrackedApp? LastLiveTrackedApp() => _tracker.LastLive();

    private void PruneDeadApps() => _tracker.Prune();

    private TrackedApp? FindLiveTrackedApp(int? pid) => _tracker.FindLive(pid);


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
        bool forceRemoveDisplay = false, bool forceAllClients = false) =>
        _releaserService.Release(
            keepDisplay, onDirty, allowDataLoss, forceRemoveDisplay, forceAllClients);


    private string CloseTrackedApp(TrackedApp app, string policy, out string? warning) =>
        _releaser.Close(app, policy, out warning);

    private void ForgetDisplay() => _display.Forget();

    private DisplayInfo? OwnDisplay() => _display.OwnDisplay();

    private bool TryRemoveDisplay() => _display.TryRemove();

    public void SaveState() => _stateKeeper.Save();

    private void RecoverFromPreviousCrash() => _stateKeeper.Recover();

    private void EnsureAppAllowed(string? path) => _allowlist.Ensure(path);


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _maintenance.Dispose();
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

    TrackedApp? IElementFinderHost.FindApp(int? pid) => FindLiveTrackedApp(pid);

    DisplayInfo IElementFinderHost.EnsureDisplay() => EnsureEnvironment();

    void IElementFinderHost.RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label) =>
        RequireWindowOnDisplay(display, hwnd, label);

    UiaHost IElementFinderHost.Uia => Uia;

    string IElementFinderHost.EnsureSnapshot() => EnsureSnapshot();

    string IElementFinderHost.RegisterElement(UiaElementInfo info, IntPtr hwnd) =>
        RegisterElement(info, hwnd);

    DisplayInfo IRegionVerifierHost.EnsureDisplay() => EnsureEnvironmentFast();

    (byte[] Bgra, int Width, int Height) IRegionVerifierHost.CaptureRegion(
        int x, int y, int width, int height) => CaptureRegion(x, y, width, height);

    FrameSnapshot? IRegionVerifierHost.FindFrame(string frameId) => FindFrame(frameId);

    FrameSnapshot? IRegionVerifierHost.FindLatestFrame(int x, int y, int width, int height) =>
        FindLatestFrame(x, y, width, height);

    IReadOnlyList<FrameSnapshot> IRegionVerifierHost.Frames() =>
        _frames.Snapshot();

    FrameSnapshot? IRegionVerifierHost.LatestFrame() => _frames.Latest();

    DisplayInfo IWaitHost.EnsureDisplay() => EnsureEnvironmentFast();

    void IWaitHost.EnterRead() => EnterRead();

    void IWaitHost.ExitRead() => ExitRead();

    bool IWaitHost.IsCancelled() => Cancellation.IsCancellationRequested;

    TrackedApp? IWaitHost.FindApp(int? pid) => FindLiveTrackedApp(pid);

    UiaHost IWaitHost.Uia => Uia;

    List<TrackedApp> IWaitHost.OwnedAppsSnapshot()
    {
        lock (_gate)
        {
            return _apps.Where(app => IsOwned(app) && IsLiveTrackedApp(app)).ToList();
        }
    }

    HashSet<int> IWaitHost.TrackedPids() => TrackedPids();

    (byte[] Bgra, int Width, int Height) IWaitHost.CaptureRegion(
        int x, int y, int width, int height) => CaptureRegion(x, y, width, height);

    FrameSnapshot? IWaitHost.ResolveBaselineFrame(JsonElement source, int x, int y, int width, int height) =>
        _regionVerifier.ResolveBaselineFrame(source, x, y, width, height);

    JsonArray IWaitHost.Frames() => _regionVerifier.FramesJson();

    MatchVisualView IWaitHost.MatchVisual(JsonElement arguments) => MatchVisual(arguments);

    void IWaitHost.ReportProgress(int done, int total, string message) =>
        ToolProgress.Report(done, total, message);

    ElementRefStore IElementResolverHost.Refs => _refs;

    UiaHost IElementResolverHost.Uia => _uiaHost;

    string IElementResolverHost.CurrentClientId => Clients.CurrentId;

    string IElementResolverHost.SnapshotId => SnapshotId;

    bool IElementResolverHost.IsTrackedPid(int pid)
    {
        lock (_gate)
        {
            return _apps.Any(app => IsOwned(app) && app.Pid == pid && IsLiveTrackedApp(app));
        }
    }

    LogRing IElementResolverHost.Log => _log;

    void IElementResolverHost.RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label) =>
        RequireWindowOnDisplay(display, hwnd, label);

    DisplayInfo IElementOperationsHost.EnsureDisplay() => EnsureEnvironment();

    UiaHost IElementOperationsHost.Uia => _uiaHost;

    ElementResolver IElementOperationsHost.Resolver => _resolver;

    ElementRefStore IElementOperationsHost.Refs => _refs;

    int IElementOperationsHost.RefCapacity => ElementRefCapacity;

    bool IElementOperationsHost.IsCancelled => Cancellation.IsCancellationRequested;

    LogRing IElementOperationsHost.Log => _log;

    TrackedApp? IElementOperationsHost.FindApp(int? pid) => FindLiveTrackedApp(pid);

    TrackedApp? IElementOperationsHost.OwnedAppForPid(int pid)
    {
        lock (_gate)
        {
            return _apps.FirstOrDefault(
                item => IsOwned(item) && item.Pid == pid && IsLiveTrackedApp(item));
        }
    }

    void IElementOperationsHost.RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label) =>
        RequireWindowOnDisplay(display, hwnd, label);

    void IElementOperationsHost.RequirePointInTrackedApp(
        DisplayInfo display, int x, int y, string label) =>
        RequirePointInTrackedApp(display, x, y, label);

    ClickView IElementOperationsHost.Click(
        int x, int y, string button, int clicks, string mode,
        bool keepForeground, bool observe, bool fallbackInput) =>
        Click(x, y, button, clicks, mode, keepForeground, observe, fallbackInput);

    TypeTextView IElementOperationsHost.TypeText(
        string text, int? pid, bool verify, int chunkChars, string channel) =>
        TypeText(text, pid, verify, chunkChars, channel);

    TextWriteResult IElementOperationsHost.WriteTextVerified(
        TrackedApp app, UiaElementHandle handle, string value, bool replace, int timeoutMs) =>
        _input.WriteTextVerified(app, handle, value, replace, timeoutMs);

    bool IElementOperationsHost.EnsureForeground(TrackedApp app) => EnsureForeground(app);

    void IElementOperationsHost.PauseFocusGuard() => PauseFocusGuard();

    void IElementOperationsHost.ResumeFocusGuard() => ResumeFocusGuard();

    void IElementOperationsHost.RestoreForeground(IntPtr previous) => RestoreForeground(previous);

    void IElementOperationsHost.ResetUia() => ResetUia();

    JsonObject IElementOperationsHost.ConvertTreeNode(
        UiaTreeNode node, IntPtr hwnd, ref string rootRef) =>
        _snapshots.ConvertTreeNode(node, hwnd, ref rootRef);

    Func<bool> IElementOperationsHost.CancellationProbe() => CancellationProbe();

    DisplayInfo IScreenTextHost.EnsureDisplay() => EnsureEnvironmentFast();

    (byte[] Bgra, int Width, int Height) IScreenTextHost.CaptureRegion(
        int x, int y, int width, int height) => CaptureRegion(x, y, width, height);

    CancellationToken IScreenTextHost.Cancellation => Cancellation;

    ClickView IScreenTextHost.Click(
        int x, int y, string button, int clicks, string mode,
        bool keepForeground, bool observe, bool fallbackInput) =>
        Click(x, y, button, clicks, mode, keepForeground, observe, fallbackInput);

    DisplayInfo IScreenCaptureHost.EnsureDisplay() => EnsureEnvironmentFast();

    (byte[] Bgra, int Width, int Height) IScreenCaptureHost.CaptureRegion(
        int x, int y, int width, int height) => CaptureRegion(x, y, width, height);

    string IScreenCaptureHost.StoreFrame(byte[] bgra, int width, int height, int x, int y) =>
        StoreFrame(bgra, width, height, x, y);

    string IScreenCaptureHost.ValidateSavePath(string path) => _paths.ValidateSavePath(path);

    TrackedAppRegistry ITrackedAppHost.Apps => _apps;

    object ITrackedAppHost.Gate => _gate;

    bool ITrackedAppHost.IsOwned(TrackedApp app) => IsOwned(app);

    LogRing ITrackedAppHost.Log => _log;

    MetricsRegistry IEnvironmentHost.Metrics => Metrics;

    void IScreenTextHost.RecordOcr() => Metrics.RecordOcr();

    object ISessionStateHost.Gate => _gate;

    StateStore ISessionStateHost.Store => _stateStore;

    DisplayStateStore ISessionStateHost.DisplayState => _display.State;

    TrackedAppRegistry ISessionStateHost.Apps => _apps;

    LogRing ISessionStateHost.Log => _log;

    string[] IAppAllowlistHost.ClientRoots => Clients.Current?.AppAllowlist ?? Array.Empty<string>();

    string IAppAllowlistHost.ResolvePath(string path) => _paths.Resolve(path);

    object IEnvironmentReleaserHost.Gate => _gate;

    void IEnvironmentReleaserHost.IncrementEpoch() => IncrementEpoch();

    int IEnvironmentReleaserHost.ActiveClientCount => Clients.ActiveCount;

    string IEnvironmentReleaserHost.CurrentClientId => Clients.CurrentId;

    TrackedAppRegistry IEnvironmentReleaserHost.Apps => _apps;

    bool IEnvironmentReleaserHost.IsOwned(TrackedApp app) => IsOwned(app);

    string IEnvironmentReleaserHost.CloseTrackedApp(
        TrackedApp app, string policy, out string? warning) =>
        CloseTrackedApp(app, policy, out warning);

    void IEnvironmentReleaserHost.ResumeFocusGuard() => ResumeFocusGuard();

    void IEnvironmentReleaserHost.ClearElementRefs(bool allClients)
    {
        if (allClients)
        {
            _refs.ClearUnsafe();
        }
        else
        {
            _refs.DropClientUnsafe(Clients.CurrentId);
        }
    }

    void IEnvironmentReleaserHost.ClearFrames(bool allClients)
    {
        if (allClients)
        {
            _frames.Clear();
        }
        else
        {
            _frames.RemoveClient(Clients.CurrentId);
        }
    }

    void IEnvironmentReleaserHost.ClearInputBlock(bool allClients)
    {
        if (allClients)
        {
            _input.ClearBlocked();
        }
        else
        {
            _input.UnblockClient(Clients.CurrentId);
        }
    }

    void IEnvironmentReleaserHost.MarkDisplaySelfHealedFalse() => _display.State.SelfHealed = false;

    void IEnvironmentReleaserHost.MarkDisplayInitialized(bool initialized) =>
        _display.State.Initialized = initialized;

    bool IEnvironmentReleaserHost.DisplayAttached => _display.State.VirtualDisplay is { Attached: true };

    bool IEnvironmentReleaserHost.TryRemoveDisplay() => TryRemoveDisplay();

    void IEnvironmentReleaserHost.StopAndDisposeVdd()
    {
        _display.Vdd.StopKeepAlive();
        _display.Vdd.Dispose();
    }

    void IEnvironmentReleaserHost.ClearStateStore() => _stateStore.Clear();

    void IEnvironmentReleaserHost.SaveState() => SaveState();

    MemoryView IEnvironmentReleaserHost.TrimIdleMemory(string reason) => TrimIdleMemory(reason);

    LogRing IEnvironmentReleaserHost.Log => _log;

    string IMaintenanceHost.ArtifactRoot => ArtifactRoot;

    bool IMaintenanceHost.Disposed => Volatile.Read(ref _disposed) != 0;

    string[] IMaintenanceHost.ArtifactDirectories
    {
        get
        {
            lock (_artifactGate)
            {
                return _artifactDirectories.Values.ToArray();
            }
        }
    }

    LogRing IMaintenanceHost.Log => _log;
}
