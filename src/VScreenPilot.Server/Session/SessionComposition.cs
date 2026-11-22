
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Services;

namespace VScreenPilot.Session;

/// <summary>会话组件的装配顺序与回调接线；从 SessionManager 构造函数下沉。</summary>
internal static class SessionComposition
{
    public static void Build(SessionManager host)
    {
        host.Snapshots = new SnapshotBuilder(
            new SnapshotStateContext(host.Uia, host.Refs, () => host.ElementOps.SnapshotId, () => SessionManager.ElementRefCapacity,
                host.Metrics, host.ElementOps.BeginSnapshot, host.ElementOps.RegisterElement),
            new SnapshotAppContext(host.EnsureEnvironmentFast, host.PruneDeadApps, host.FindLiveTrackedApp,
                host.InputOps.RequireWindowOnDisplay, () => host._apps.Where(host.ClientOps.IsOwned).ToList(), TrackedAppTracker.IsLive),
            new SnapshotCaptureContext(host.CaptureOps.CaptureRegion,
                (scale, previewScale) => host.CaptureOps.CaptureScreenEx(new CaptureRequest(
                    null, null, null, null, scale, null, StoreFrame: false, previewScale)),
                host.CancellationProbe));
        host._display = new VirtualDisplayController(
            host._gate, host._log, host.ProfileName,
            () => Volatile.Read(ref host._disposed) != 0,
            host.OnDisplayReady,
            host.OnDisplayRemoved,
            host.LifecycleOps.SaveState,
            SessionManager.VirtualWidth,
            SessionManager.VirtualHeight,
            SessionManager.VirtualRefresh);
        host._environment = new EnvironmentReporter(
            new EnvironmentRuntimeContext(host.Metrics, host._gate, () => host._display.State.Initialized, host.ProfileName,
                () => host.Epoch, host._job, host.ScreenLeaseProvider),
            new EnvironmentClientContext(host.Clients, () => host.ClientOps.ClientSummariesJson()),
            new EnvironmentAppContext(host._apps, host.ClientOps.IsOwned, TrackedAppTracker.IsLive, host.PruneDeadApps, host.Uia),
            new EnvironmentHealContext(() => host._display.State.SelfHealed,
                () => host._display.State.SelfHealCount, () => host._display.State.LastSelfHealReason,
                () => host._display.State.AdoptedOrphan, () => host._display.State.RecreateCount,
                () => host._display.State.VddReopenCount, () => host._display.State.PositionFallback),
            new EnvironmentDisplayContext(host._display.Vdd, () => host._display.State.Index,
                () => host._display.State.DeviceName, host.EnsureEnvironment, host.LifecycleOps.OwnDisplay));
        host._releaser = new AppReleaser(host._log, host._uiaHost, host.ResetUia);
        host._launcher = new AppLauncher(
            new LaunchSessionContext(host._log, host._job, host.Clients, host._gate, host.LifecycleOps.SaveState),
            new LaunchAppContext(host._apps, host.Uia, host.EnsureEnvironment, host._paths.Resolve,
                host.LifecycleOps.EnsureAppAllowed, host.ClientOps.IsOwned, TrackedAppTracker.IsLive),
            new LaunchFocusContext(previous => host.InputOps.RestoreForeground(previous, logFailure: true),
                host.FocusOps.StartFocusGuard, () => host.FocusOps.PauseFocusGuard(), host.FocusOps.ResumeFocusGuard, host.InputOps.EnsureForeground));
        host.VisualMatch = new VisualMatchService(new VisualContext(host.EnsureEnvironment, host.CaptureOps.CaptureRegion, host._paths.ValidatePngPath, () => host.Cancellation.IsCancellationRequested));
        host._finder = new ElementFinder(new ElementFinderContext(host.FindLiveTrackedApp, host.EnsureEnvironment, host.InputOps.RequireWindowOnDisplay, host.Uia, host.ElementOps.EnsureSnapshot, host.ElementOps.RegisterElement));
        host._regionVerifier = new RegionVerifier(new RegionVerifierContext(host.EnsureEnvironmentFast, host.CaptureOps.CaptureRegion, host.CaptureOps.FindFrame, host.CaptureOps.FindLatestFrame, () => host._frames.Latest(), () => host._frames.Snapshot()));
        host._waits = new WaitService(new WaitDisplayContext(host.EnsureEnvironmentFast, host.CaptureOps.CaptureRegion), new WaitSessionContext(host.EnterRead, host.ExitRead, () => host.Cancellation.IsCancellationRequested, ToolProgress.Report), new WaitAppContext(host.FindLiveTrackedApp, host.LifecycleOps.OwnedAppsSnapshotCore, host.VerifyOps.TrackedPids, host.Uia), new WaitFrameContext((source, x, y, w, h) => host._regionVerifier.ResolveBaselineFrame(source, x, y, w, h), host._regionVerifier.FramesJson, host.VisualOps.MatchVisual));
        host._resolver = new ElementResolver(new ElementResolverContext(host.Refs, () => host.Clients.CurrentId, () => host.ElementOps.SnapshotId, host._uiaHost, host.LifecycleOps.IsTrackedPidCore, host.InputOps.RequireWindowOnDisplay, host._log));
        host._input = new InputRouter(
            new InputSessionContext(host._log, host.Clients, () => host.Cancellation),
            new InputFocusContext(host.InputOps.EnsureForeground, host.InputOps.IsAppForeground, previous => host.InputOps.RestoreForeground(previous),
                host.FocusOps.PauseFocusGuard, host.FocusOps.ResumeFocusGuard, () => host._cursorGuard!.Pause(), () => host._cursorGuard!.Resume()),
            new InputDisplayContext(host.EnsureEnvironmentFast, host.EnsureEnvironment,
                host.InputOps.RequireWindowOnDisplay, host.InputOps.RequirePointInDisplay, host.ObserveOps.CaptureSignature,
                () => EnvironmentProbe.IsInputDesktopAvailable()),
            new InputElementContext(host._resolver.Require, host._resolver.Resolve, host.LifecycleOps.FindTrackedAppAtWindowCore,
                host.InputOps.RequirePointInTrackedApp, host.LastLiveTrackedApp, host.FindLiveTrackedApp),
            new InputUiaContext(host.Uia, host.ResetUia, host.ObserveOps.BuildObserved));
        host._elements = new ElementOperations(
            new ElementStateContext(host._uiaHost, host._resolver, host.Refs, host.ElementOps.RegisterElement,
                SessionManager.ElementRefCapacity, host._log, host.Snapshots.ConvertTreeNode),
            new ElementSnapshotContext(host.ElementOps.EnsureSnapshot, host.ElementOps.BeginSnapshot,
                () => host.Cancellation.IsCancellationRequested, host.CancellationProbe),
            new ElementAppContext(host.EnsureEnvironment, host.FindLiveTrackedApp, host.LifecycleOps.OwnedAppForPidCore,
                host.InputOps.RequireWindowOnDisplay, (display, x, y, label) => { _ = host.InputOps.RequirePointInTrackedApp(display, x, y, label); }),
            new ElementInputContext(host.InputOps.Click, host.InputOps.TypeText, (app, handle, value, replace, timeout) => host._input.TextOps.WriteTextVerified(app, handle, value, replace, timeout), host.ResetUia),
            new ElementFocusContext(host.InputOps.EnsureForeground, () => host.FocusOps.PauseFocusGuard(),
                host.FocusOps.ResumeFocusGuard, previous => host.InputOps.RestoreForeground(previous)));
        host._screenText = new ScreenTextService(new ScreenTextContext(host.EnsureEnvironmentFast, host.CaptureOps.CaptureRegion, () => host.Cancellation, host.Metrics.RecordOcr, host.InputOps.Click));
        host._capture = new ScreenCaptureService(new ScreenCaptureContext(host.EnsureEnvironmentFast, host.CaptureOps.CaptureRegion, host.CaptureOps.StoreFrame, host._paths.ValidateSavePath));
        host.FocusGuard = new FocusGuard(new FocusContext(host.FocusOps.IsTrackedWindow, host.FocusOps.IsAnyClientWindow, target => host.InputOps.RestoreForeground(target, logFailure: false), host._log));
        host._tracker = new TrackedAppTracker(new TrackedAppContext(host._apps, host._gate, host.ClientOps.IsOwned, host._log));
        host._stateKeeper = new SessionStateKeeper(new SessionStateContext(host._gate, host._stateStore, host._display.State, host._apps, host._log));
        host._allowlist = new AppAllowlistPolicy(new AppAllowlistContext(() => host.Clients.Current?.AppAllowlist ?? Array.Empty<string>(), host._paths.Resolve));
        host._releaserService = new EnvironmentReleaser(
            new ReleaseSessionContext(host._gate, host.IncrementEpoch, () => host.Clients.ActiveCount, host._log),
            new ReleaseAppsContext(host._apps, host.ClientOps.IsOwned,
                (app, policy) =>
                {
                    string outcome = host.LifecycleOps.CloseTrackedApp(app, policy, out string? warning);
                    return (outcome, warning);
                },
                host.FocusOps.ResumeFocusGuard),
            new ReleaseDisplayContext(
                host.LifecycleOps.ClearElementRefsCore, host.LifecycleOps.ClearFramesCore, host.LifecycleOps.ClearInputBlockCore,
                () => host._display.State!.SelfHealed = false,
                initialized => host._display.State.Initialized = initialized,
                () => host._display.State!.VirtualDisplay is { Attached: true },
                host.LifecycleOps.TryRemoveDisplay,
                host.LifecycleOps.StopAndDisposeVddCore),
            new ReleaseStateContext(host.LifecycleOps.TrimIdleMemory, host.LifecycleOps.SaveState, () => host._stateStore.Clear()));
        host.MaintenanceService = new MaintenanceService(new MaintenanceContext(host.ArtifactRoot, () => Volatile.Read(ref host._disposed) != 0, () => host.LifecycleOps.ArtifactDirectorySnapshot(), host._log));
        host._log.ClientIdProvider = () => host.Clients.CurrentId;
        AppLauncher.CleanupStaleProfiles();
        host.ArtifactOps.SweepOldArtifacts();
        host.LifecycleOps.RecoverFromPreviousCrash();
        host.ArtifactOps.StartMaintenance();
    }
}
