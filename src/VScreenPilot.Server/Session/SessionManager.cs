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
using VScreenPilot.Perception;
using VScreenPilot.Scripting;
using VScreenPilot.Verification;

namespace VScreenPilot.Session;

/// <summary>
/// 会话生命周期：互斥体、按需挂载虚拟屏、窗口归位、统一清理。
/// </summary>
internal sealed class SessionManager : IDisposable
{
    internal static readonly Options.VspOptions HostOptions = Options.VspOptions.FromEnvironment();

    internal static readonly int VirtualWidth = HostOptions.DisplayWidth;
    internal static readonly int VirtualHeight = HostOptions.DisplayHeight;
    internal static readonly int VirtualRefresh = HostOptions.DisplayRefresh;

    internal readonly object _gate = new();

    internal readonly ElementRefStore Refs = new();
    internal readonly FocusGuard FocusGuard;
    internal readonly ScreenCapture ScreenCapture = new();
    internal readonly SnapshotBuilder Snapshots;
    internal readonly VisualMatchService VisualMatch;
    internal readonly ScriptRunner ScriptRunner = new();
    internal readonly MaintenanceService MaintenanceService;
    internal ObserveSampler? ObserveSampler;
    internal const int ElementRefCapacity = ElementRefStore.Capacity;

    internal readonly SessionAppOps AppOps;
    internal readonly SessionArtifactOps ArtifactOps;
    internal readonly SessionCaptureOps CaptureOps;
    internal readonly SessionClientOps ClientOps;
    internal readonly SessionElementOps ElementOps;
    internal readonly SessionFocusOps FocusOps;
    internal readonly SessionInputOps InputOps;
    internal readonly SessionLifecycleOps LifecycleOps;
    internal readonly SessionObserveOps ObserveOps;
    internal readonly SessionOcrOps OcrOps;
    internal readonly SessionScriptOps ScriptOps;
    internal readonly SessionSnapshotOps SnapshotOps;
    internal readonly SessionVerifyOps VerifyOps;
    internal readonly SessionVisualOps VisualOps;
    internal Func<string?>? LeaseHolderProvider { get; set; }
    internal Func<System.Text.Json.Nodes.JsonObject>? ScreenLeaseProvider { get; set; }

    /// <summary>本会话的客户端登记处；每个 SessionManager 一份，不使用进程级静态。</summary>
    internal ClientSessionRegistry Clients { get; } = new();

    internal readonly ClientPathPolicy _paths;
    internal readonly EnvironmentReporter _environment;

    internal readonly SessionGuard _guard = new();
    internal readonly LogRing _log;
    internal readonly StateStore _stateStore;
    internal readonly JobObject _job;
    internal readonly TrackedAppRegistry _apps;
    internal readonly bool _allowScript;
    internal readonly AppAllowlistPolicy _allowlist;
    internal readonly EnvironmentReleaser _releaserService;

    internal int _disposed;

    internal readonly FrameStore _frames = new();
    // 按区域保留更多基线，避免只截另一区域就把当前区域基线挤掉。
    internal const int MaxFrames = 8;

    internal readonly UiaHost _uiaHost = new();
    internal readonly VirtualDisplayController _display;
    internal readonly AppReleaser _releaser;
    internal readonly AppLauncher _launcher;
    internal readonly InputRouter _input;
    internal readonly ElementFinder _finder;
    internal readonly TrackedAppTracker _tracker;
    internal readonly SessionStateKeeper _stateKeeper;
    internal readonly RegionVerifier _regionVerifier;
    internal readonly WaitService _waits;
    internal readonly ElementResolver _resolver;
    internal readonly ElementOperations _elements;
    internal readonly ScreenTextService _screenText;
    internal readonly ScreenCaptureService _capture;
    internal CursorGuard? _cursorGuard;
    // 每调用一个异步流：并发工具不会互相清掉取消令牌（旧实现是单个字段）。
    internal readonly AsyncLocal<CancellationTokenSource?> _cancellation = new();
    internal long _epoch;

    internal bool DisplayPositionFallback
    {
        get => _display.State.PositionFallback;
        set => _display.State.PositionFallback = value;
    }



    internal void OnDisplayReady(DisplayInfo display)
    {
        _cursorGuard?.Dispose();
        _cursorGuard = new CursorGuard(display, _log.Info, _log.Warn);
        _cursorGuard.Start();
    }

    internal void OnDisplayRemoved()
    {
        _cursorGuard?.Dispose();
        _cursorGuard = null;
    }

    internal UiaHost Uia => _uiaHost;

    /// <summary>
    /// 放弃当前 UIA 代际并换新实例。只在 UIA 调用超时/疑似卡死时调用：
    /// 旧代际的工作线程会把手上的 COM 调用做完再自行释放，不在本线程 Dispose。
    /// </summary>
    internal void ResetUia()
    {
        if (_uiaHost.Retire())
        {
            _log.Warn("UIA 服务疑似卡死，已切换到新代际（旧实例后台收尾释放）");
        }
    }

    /// <summary>当前工具调用的取消令牌（由 MCP 层设置，供 wait_for 等长操作观察）。</summary>
    internal CancellationToken Cancellation
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

    internal void SetCancellation(CancellationTokenSource? source) => _cancellation.Value = source;
    /// <summary>
    /// 把当前异步流的取消令牌固化成回调：UIA 宿主在自己的 MTA 线程上调用它，
    /// AsyncLocal 不会跨手工线程流动，必须在调用方先取令牌。
    /// </summary>
    internal Func<bool> CancellationProbe()
    {
        CancellationToken token = Cancellation;
        return () => token.IsCancellationRequested;
    }


    /// <summary>会话代际：release_environment 之后递增，旧 handler 的结果会被丢弃。</summary>
    internal long Epoch => Clients.Current is { } client
        ? Interlocked.Read(ref client.Epoch)
        : Interlocked.Read(ref _epoch);

    internal void IncrementEpoch()
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

    internal void EnterRead() => _guard.EnterRead();

    internal void ExitRead() => _guard.ExitRead();

    internal bool TryEnterExclusive(int milliseconds) =>
        _guard.TryEnterExclusive(milliseconds);

    internal void ExitExclusive() => _guard.ExitExclusive();

    /// <summary>多会话 profile 名（--profile / VSP_PROFILE）；默认 default。</summary>
    internal string ProfileName { get; } = Profile.Resolve();

    /// <summary>进程内唯一的写调度器（daemon 内所有连接共享）。</summary>
    internal WriteScheduler Scheduler { get; } = new();

    /// <summary>进程内唯一的屏幕租约（daemon 内所有连接共享）。</summary>
    internal ScreenLease Lease { get; } = new();

    internal readonly object _artifactGate = new();
    internal readonly Dictionary<string, string> _artifactDirectories = new(StringComparer.Ordinal);

    internal string ArtifactDirectory => ArtifactOps.Directory;

    internal string ArtifactRoot => ArtifactOps.Root;

    internal SessionManager(
        LogRing log, StateStore stateStore, JobObject job, bool allowScript = false)
    {
        _log = log;
        _stateStore = stateStore;
        _job = job;
        _allowScript = allowScript;

        AppOps = new SessionAppOps(this);
        ArtifactOps = new SessionArtifactOps(this);
        CaptureOps = new SessionCaptureOps(this);
        ClientOps = new SessionClientOps(this);
        ElementOps = new SessionElementOps(this);
        FocusOps = new SessionFocusOps(this);
        InputOps = new SessionInputOps(this);
        LifecycleOps = new SessionLifecycleOps(this);
        ObserveOps = new SessionObserveOps(this);
        OcrOps = new SessionOcrOps(this);
        ScriptOps = new SessionScriptOps(this);
        SnapshotOps = new SessionSnapshotOps(this);
        VerifyOps = new SessionVerifyOps(this);
        VisualOps = new SessionVisualOps(this);

        _paths = new ClientPathPolicy(Clients);
        _apps = new TrackedAppRegistry(_gate);
        Snapshots = new SnapshotBuilder(
            new SnapshotStateContext(Uia, Refs, () => ElementOps.SnapshotId, () => ElementRefCapacity,
                Metrics, ElementOps.BeginSnapshot, ElementOps.RegisterElement),
            new SnapshotAppContext(EnsureEnvironmentFast, PruneDeadApps, FindLiveTrackedApp,
                InputOps.RequireWindowOnDisplay, () => _apps.Where(ClientOps.IsOwned).ToList(), TrackedAppTracker.IsLive),
            new SnapshotCaptureContext(CaptureOps.CaptureRegion,
                (scale, previewScale) => CaptureOps.CaptureScreenEx(null, null, null, null, scale,
                    savePath: null, storeFrame: false, previewScale: previewScale),
                CancellationProbe));
        _display = new VirtualDisplayController(
            _gate, log, ProfileName,
            () => Volatile.Read(ref _disposed) != 0,
            OnDisplayReady,
            OnDisplayRemoved,
            LifecycleOps.SaveState,
            VirtualWidth,
            VirtualHeight,
            VirtualRefresh);
        _environment = new EnvironmentReporter(
            new EnvironmentRuntimeContext(Metrics, _gate, () => _display.State.Initialized, ProfileName,
                () => Epoch, _job, ScreenLeaseProvider),
            new EnvironmentClientContext(Clients, () => ClientOps.ClientSummariesJson()),
            new EnvironmentAppContext(_apps, ClientOps.IsOwned, TrackedAppTracker.IsLive, PruneDeadApps, Uia),
            new EnvironmentHealContext(() => _display.State.SelfHealed,
                () => _display.State.SelfHealCount, () => _display.State.LastSelfHealReason,
                () => _display.State.AdoptedOrphan, () => _display.State.RecreateCount,
                () => _display.State.VddReopenCount, () => _display.State.PositionFallback),
            new EnvironmentDisplayContext(_display.Vdd, () => _display.State.Index,
                () => _display.State.DeviceName, EnsureEnvironment, LifecycleOps.OwnDisplay));
        _releaser = new AppReleaser(log, _uiaHost, ResetUia);
        _launcher = new AppLauncher(
            new LaunchSessionContext(_log, _job, Clients, _gate, LifecycleOps.SaveState),
            new LaunchAppContext(_apps, Uia, EnsureEnvironment, _paths.Resolve,
                LifecycleOps.EnsureAppAllowed, ClientOps.IsOwned, TrackedAppTracker.IsLive),
            new LaunchFocusContext(previous => InputOps.RestoreForeground(previous, logFailure: true),
                FocusOps.StartFocusGuard, () => FocusOps.PauseFocusGuard(), FocusOps.ResumeFocusGuard, InputOps.EnsureForeground));
        VisualMatch = new VisualMatchService(new VisualContext(EnsureEnvironment, CaptureOps.CaptureRegion, _paths.ValidatePngPath, () => Cancellation.IsCancellationRequested));
        _finder = new ElementFinder(new ElementFinderContext(FindLiveTrackedApp, EnsureEnvironment, InputOps.RequireWindowOnDisplay, Uia, ElementOps.EnsureSnapshot, ElementOps.RegisterElement));
        _regionVerifier = new RegionVerifier(new RegionVerifierContext(EnsureEnvironmentFast, CaptureOps.CaptureRegion, CaptureOps.FindFrame, CaptureOps.FindLatestFrame, () => _frames.Latest(), () => _frames.Snapshot()));
        _waits = new WaitService(new WaitDisplayContext(EnsureEnvironmentFast, CaptureOps.CaptureRegion), new WaitSessionContext(EnterRead, ExitRead, () => Cancellation.IsCancellationRequested, ToolProgress.Report), new WaitAppContext(FindLiveTrackedApp, LifecycleOps.OwnedAppsSnapshotCore, VerifyOps.TrackedPids, Uia), new WaitFrameContext((source, x, y, w, h) => _regionVerifier.ResolveBaselineFrame(source, x, y, w, h), _regionVerifier.FramesJson, VisualOps.MatchVisual));
        _resolver = new ElementResolver(new ElementResolverContext(Refs, () => Clients.CurrentId, () => ElementOps.SnapshotId, _uiaHost, LifecycleOps.IsTrackedPidCore, InputOps.RequireWindowOnDisplay, _log));
        _input = new InputRouter(
            new InputSessionContext(_log, Clients, () => Cancellation),
            new InputFocusContext(InputOps.EnsureForeground, InputOps.IsAppForeground, previous => InputOps.RestoreForeground(previous),
                FocusOps.PauseFocusGuard, FocusOps.ResumeFocusGuard, () => _cursorGuard!.Pause(), () => _cursorGuard!.Resume()),
            new InputDisplayContext(EnsureEnvironmentFast, EnsureEnvironment,
                InputOps.RequireWindowOnDisplay, InputOps.RequirePointInDisplay, ObserveOps.CaptureSignature,
                () => EnvironmentProbe.IsInputDesktopAvailable()),
            new InputElementContext(_resolver.Require, _resolver.Resolve, LifecycleOps.FindTrackedAppAtWindowCore,
                InputOps.RequirePointInTrackedApp, LastLiveTrackedApp, FindLiveTrackedApp),
            new InputUiaContext(Uia, ResetUia, ObserveOps.BuildObserved));
        _elements = new ElementOperations(
            new ElementStateContext(_uiaHost, _resolver, Refs, ElementOps.RegisterElement,
                ElementRefCapacity, _log, Snapshots.ConvertTreeNode),
            new ElementSnapshotContext(ElementOps.EnsureSnapshot, ElementOps.BeginSnapshot,
                () => Cancellation.IsCancellationRequested, CancellationProbe),
            new ElementAppContext(EnsureEnvironment, FindLiveTrackedApp, LifecycleOps.OwnedAppForPidCore,
                InputOps.RequireWindowOnDisplay, (display, x, y, label) => { _ = InputOps.RequirePointInTrackedApp(display, x, y, label); }),
            new ElementInputContext(InputOps.Click, InputOps.TypeText, (app, handle, value, replace, timeout) => _input.WriteTextVerified(app, handle, value, replace, timeout), ResetUia),
            new ElementFocusContext(InputOps.EnsureForeground, () => FocusOps.PauseFocusGuard(),
                FocusOps.ResumeFocusGuard, previous => InputOps.RestoreForeground(previous)));
        _screenText = new ScreenTextService(new ScreenTextContext(EnsureEnvironmentFast, CaptureOps.CaptureRegion, () => Cancellation, Metrics.RecordOcr, InputOps.Click));
        _capture = new ScreenCaptureService(new ScreenCaptureContext(EnsureEnvironmentFast, CaptureOps.CaptureRegion, CaptureOps.StoreFrame, _paths.ValidateSavePath));
        FocusGuard = new FocusGuard(new FocusContext(FocusOps.IsTrackedWindow, FocusOps.IsAnyClientWindow, target => InputOps.RestoreForeground(target, logFailure: false), _log));
        _tracker = new TrackedAppTracker(new TrackedAppContext(_apps, _gate, ClientOps.IsOwned, _log));
        _stateKeeper = new SessionStateKeeper(new SessionStateContext(_gate, _stateStore, _display.State, _apps, _log));
        _allowlist = new AppAllowlistPolicy(new AppAllowlistContext(() => Clients.Current?.AppAllowlist ?? Array.Empty<string>(), _paths.Resolve));
        _releaserService = new EnvironmentReleaser(
            new ReleaseSessionContext(_gate, IncrementEpoch, () => Clients.ActiveCount, _log),
            new ReleaseAppsContext(_apps, ClientOps.IsOwned,
                (app, policy) =>
                {
                    string outcome = LifecycleOps.CloseTrackedApp(app, policy, out string? warning);
                    return (outcome, warning);
                },
                FocusOps.ResumeFocusGuard),
            new ReleaseDisplayContext(
                LifecycleOps.ClearElementRefsCore, LifecycleOps.ClearFramesCore, LifecycleOps.ClearInputBlockCore,
                () => _display.State!.SelfHealed = false,
                initialized => _display.State.Initialized = initialized,
                () => _display.State!.VirtualDisplay is { Attached: true },
                LifecycleOps.TryRemoveDisplay,
                LifecycleOps.StopAndDisposeVddCore),
            new ReleaseStateContext(LifecycleOps.TrimIdleMemory, LifecycleOps.SaveState, () => _stateStore.Clear()));
        MaintenanceService = new MaintenanceService(new MaintenanceContext(ArtifactRoot, () => Volatile.Read(ref _disposed) != 0, () => LifecycleOps.ArtifactDirectorySnapshot(), _log));
        _log.ClientIdProvider = () => Clients.CurrentId;
        AppLauncher.CleanupStaleProfiles();
        ArtifactOps.SweepOldArtifacts();
        LifecycleOps.RecoverFromPreviousCrash();
        ArtifactOps.StartMaintenance();
    }

    internal LogRing Log => _log;

    internal MetricsRegistry Metrics { get; } = new();

    internal bool AllowScript => _allowScript;

    internal bool Initialized => _display.State.Initialized;

    internal DisplayInfo? VirtualDisplay => _display.State.VirtualDisplay;

    internal IReadOnlyList<TrackedApp> Apps => _apps.Snapshot();

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
    internal TrackedApp? LastLiveTrackedApp() => _tracker.LastLive();

    internal void PruneDeadApps() => _tracker.Prune();

    internal TrackedApp? FindLiveTrackedApp(int? pid) => _tracker.FindLive(pid);


    internal DisplayInfo EnsureEnvironmentFast() => _display.EnsureFast();

    internal DisplayInfo EnsureEnvironment() => _display.Ensure();

    internal void Dispose() => LifecycleOps.Dispose();

    void IDisposable.Dispose() => Dispose();
}
