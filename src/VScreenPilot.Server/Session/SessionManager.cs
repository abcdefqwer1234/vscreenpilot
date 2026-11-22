using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>
/// 会话生命周期：互斥体、按需挂载虚拟屏、窗口归位、统一清理。
/// </summary>
internal sealed partial class SessionManager : IDisposable, IEnvironmentReleaserHost, IElementOperationsHost
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
    public string ArtifactRoot => ArtifactStore.RootFor(ProfileName);

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
        _visual = new VisualMatchService(new VisualContext(EnsureEnvironment, CaptureRegion, _paths.ValidatePngPath, () => Cancellation.IsCancellationRequested));
        _finder = new ElementFinder(new ElementFinderContext(FindLiveTrackedApp, EnsureEnvironment, RequireWindowOnDisplay, Uia, EnsureSnapshot, RegisterElement));
        _regionVerifier = new RegionVerifier(new RegionVerifierContext(EnsureEnvironmentFast, CaptureRegion, FindFrame, FindLatestFrame, () => _frames.Latest(), () => _frames.Snapshot()));
        _waits = new WaitService(new WaitDisplayContext(EnsureEnvironmentFast, CaptureRegion), new WaitSessionContext(EnterRead, ExitRead, () => Cancellation.IsCancellationRequested, ToolProgress.Report), new WaitAppContext(FindLiveTrackedApp, OwnedAppsSnapshotCore, TrackedPids, Uia), new WaitFrameContext((source, x, y, w, h) => _regionVerifier.ResolveBaselineFrame(source, x, y, w, h), _regionVerifier.FramesJson, MatchVisual));
        _resolver = new ElementResolver(new ElementResolverContext(_refs, () => Clients.CurrentId, () => SnapshotId, _uiaHost, IsTrackedPidCore, RequireWindowOnDisplay, _log));
        _elements = new ElementOperations(this);
        _screenText = new ScreenTextService(new ScreenTextContext(EnsureEnvironmentFast, CaptureRegion, () => Cancellation, Metrics.RecordOcr, Click));
        _capture = new ScreenCaptureService(new ScreenCaptureContext(EnsureEnvironmentFast, CaptureRegion, StoreFrame, _paths.ValidateSavePath));
        _focus = new FocusGuard(new FocusContext(IsTrackedWindow, IsAnyClientWindow, target => RestoreForeground(target, logFailure: false), _log));
        _tracker = new TrackedAppTracker(new TrackedAppContext(_apps, _gate, IsOwned, _log));
        _stateKeeper = new SessionStateKeeper(new SessionStateContext(_gate, _stateStore, _display.State, _apps, _log));
        _allowlist = new AppAllowlistPolicy(new AppAllowlistContext(() => Clients.Current?.AppAllowlist ?? Array.Empty<string>(), _paths.Resolve));
        _releaserService = new EnvironmentReleaser(this);
        _maintenance = new MaintenanceService(new MaintenanceContext(ArtifactRoot, () => Volatile.Read(ref _disposed) != 0, () => ArtifactDirectorySnapshot(), _log));
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
    private TrackedApp? LastLiveTrackedApp() => _tracker.LastLive();

    private void PruneDeadApps() => _tracker.Prune();

    private TrackedApp? FindLiveTrackedApp(int? pid) => _tracker.FindLive(pid);


    public DisplayInfo EnsureEnvironmentFast() => _display.EnsureFast();

    public DisplayInfo EnsureEnvironment() => _display.Ensure();

}
