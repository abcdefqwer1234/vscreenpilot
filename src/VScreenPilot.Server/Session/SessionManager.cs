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
    internal FocusGuard FocusGuard = null!;
    internal readonly ScreenCapture ScreenCapture = new();
    internal SnapshotBuilder Snapshots = null!;
    internal VisualMatchService VisualMatch = null!;
    internal readonly ScriptRunner ScriptRunner = new();
    internal MaintenanceService MaintenanceService = null!;
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

    internal ClientPathPolicy _paths = null!;
    internal EnvironmentReporter _environment = null!;

    internal readonly SessionGuard _guard = new();
    internal readonly LogRing _log;
    internal readonly StateStore _stateStore;
    internal readonly JobObject _job;
    internal TrackedAppRegistry _apps = null!;
    internal readonly bool _allowScript;
    internal AppAllowlistPolicy _allowlist = null!;
    internal EnvironmentReleaser _releaserService = null!;

    internal int _disposed;

    internal readonly FrameStore _frames = new();
    // 按区域保留更多基线，避免只截另一区域就把当前区域基线挤掉。
    internal const int MaxFrames = 8;

    internal readonly UiaHost _uiaHost = new();
    internal VirtualDisplayController _display = null!;
    internal AppReleaser _releaser = null!;
    internal AppLauncher _launcher = null!;
    internal InputRouter _input = null!;
    internal ElementFinder _finder = null!;
    internal TrackedAppTracker _tracker = null!;
    internal SessionStateKeeper _stateKeeper = null!;
    internal RegionVerifier _regionVerifier = null!;
    internal WaitService _waits = null!;
    internal ElementResolver _resolver = null!;
    internal ElementOperations _elements = null!;
    internal ScreenTextService _screenText = null!;
    internal ScreenCaptureService _capture = null!;
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
        SessionComposition.Build(this);
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
