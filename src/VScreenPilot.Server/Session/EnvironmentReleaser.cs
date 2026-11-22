using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>release_environment 所需的最小宿主接口。</summary>
internal sealed record ReleaseSessionContext(
    object Gate,
    Action IncrementEpoch,
    Func<int> ActiveClientCount,
    LogRing Log);

internal sealed record ReleaseAppsContext(
    TrackedAppRegistry Apps,
    Func<TrackedApp, bool> IsOwned,
    Func<TrackedApp, string, (string Outcome, string? Warning)> CloseTrackedApp,
    Action ResumeFocusGuard);

internal sealed record ReleaseDisplayContext(
    Action<bool> ClearElementRefs,
    Action<bool> ClearFrames,
    Action<bool> ClearInputBlock,
    Action MarkDisplaySelfHealedFalse,
    Action<bool> MarkDisplayInitialized,
    Func<bool> DisplayAttached,
    Func<bool> TryRemoveDisplay,
    Action StopAndDisposeVdd);

internal sealed record ReleaseStateContext(
    Func<string, MemoryView> TrimIdleMemory,
    Action SaveState,
    Action ClearStateStore);

/// <summary>release_environment：按策略关闭/归位应用，释放 ref、帧、输入与虚拟屏。</summary>
internal sealed class EnvironmentReleaser
{
    private readonly ReleaseSessionContext _session;
    private readonly ReleaseAppsContext _apps;
    private readonly ReleaseDisplayContext _display;
    private readonly ReleaseStateContext _state;

    public EnvironmentReleaser(ReleaseSessionContext session, ReleaseAppsContext apps, ReleaseDisplayContext display, ReleaseStateContext state)
    {
        _session = session;
        _apps = apps;
        _display = display;
        _state = state;
    }

    private sealed class ReleaseState
    {
        public List<int> Closed { get; } = new();
        public List<int> Restored { get; } = new();
        public List<int> Forced { get; } = new();
        public List<int> Kept { get; } = new();
        public List<int> Saved { get; } = new();
        public List<int> Discarded { get; } = new();
        public JsonArray Warnings { get; } = new();
    }

    public ReleaseReport Release(
        bool keepDisplay, string onDirty = "abort", bool allowDataLoss = false,
        bool forceRemoveDisplay = false, bool forceAllClients = false)
    {
        lock (_session.Gate)
        {
            var state = new ReleaseState();
            onDirty = ValidateOnDirty(onDirty, allowDataLoss);

            // 校验通过后再推进代际：一次被拒绝的 release 不应让在途结果作废。
            _session.IncrementEpoch();

            bool keepDisplayEffective = ResolveKeepDisplay(
                keepDisplay, onDirty, forceRemoveDisplay, state.Warnings);

            bool removed = false;

            try
            {
                ReleaseApps(state, onDirty, forceAllClients);
            }
            finally
            {
                removed = FinishRelease(state, onDirty, forceAllClients, keepDisplayEffective);
            }

            MemoryView memory = _state.TrimIdleMemory("release");
            return BuildReport(state, removed, keepDisplayEffective, onDirty, memory);
        }
    }

    private static string ValidateOnDirty(string? onDirty, bool allowDataLoss)
    {
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
                hint: "先保存；确认可丢弃后再传 allow_data_loss=true，或改用 on_dirty=save/abort",
                stage: "release");
        }

        return onDirty;
    }

    /// <summary>abort 必须保留虚拟屏；其他客户端仍在使用时也不能摘屏。</summary>
    private bool ResolveKeepDisplay(
        bool keepDisplay, string onDirty, bool forceRemoveDisplay, JsonArray warnings)
    {
        bool keepDisplayEffective = keepDisplay;

        if (onDirty == "abort" && !keepDisplayEffective)
        {
            keepDisplayEffective = true;
            warnings.Add("on_dirty=abort 需要保留虚拟屏，已忽略 keep_display=false");
        }

        if (!keepDisplayEffective && _session.ActiveClientCount() > 1 && !forceRemoveDisplay)
        {
            keepDisplayEffective = true;
            warnings.Add("其他客户端仍在使用虚拟屏，已忽略 keep_display=false");
        }

        return keepDisplayEffective;
    }

    private void ReleaseApps(ReleaseState state, string onDirty, bool forceAllClients)
    {
        IEnumerable<TrackedApp> releaseApps = forceAllClients
            ? _apps.Apps.Snapshot()
            : _apps.Apps.Where(_apps.IsOwned);

        foreach (TrackedApp app in releaseApps.Reverse().ToList())
        {
            try
            {
                CloseOrRestoreApp(state, app, onDirty);
            }
            catch (ArgumentException)
            {
                if (app.StartedProcess)
                {
                    state.Closed.Add(app.Pid);
                }
            }
            catch (Exception exception)
            {
                _session.Log.Warn($"清理应用 {app.Pid} 失败: {exception.Message}");
                state.Warnings.Add($"清理 {app.Pid} 失败: {exception.Message}");
            }
        }
    }

    private void CloseOrRestoreApp(ReleaseState state, TrackedApp app, string onDirty)
    {
        bool windowAlive = app.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(app.Hwnd);

        if (windowAlive)
        {
            CloseOrRestoreWindow(state, app, onDirty);
        }

        ReclaimResidualProcess(app, windowAlive);
    }

    private void CloseOrRestoreWindow(ReleaseState state, TrackedApp app, string onDirty)
    {
        if (app.Owned && app.CloseWindowOnRelease)
        {
            CloseTrackedWindow(state, app, onDirty);
        }
        else if (app.PlacementSaved)
        {
            // 接管（非本会话启动）的窗口只归位、不关闭。
            WindowManager.SetPlacement(app.Hwnd, app.Placement);
            state.Restored.Add(app.Pid);
            state.Kept.Add(app.Pid);
        }

        if (app.StartedProcess && app.TempProfile is not null)
        {
            AppTermination.KillProcessTreeIfMatches(app.Pid, app.ExePath);
            state.Forced.Add(app.Pid);
        }
    }

    private void CloseTrackedWindow(ReleaseState state, TrackedApp app, string onDirty)
    {
        (string outcome, string? warning) = _apps.CloseTrackedApp(app, onDirty);
        RecordOutcome(state, app.Pid, outcome);

        if (warning is not null)
        {
            state.Warnings.Add(warning);
        }
    }

    private static void RecordOutcome(ReleaseState state, int pid, string outcome)
    {
        switch (outcome)
        {
            case "closed":
                state.Closed.Add(pid);
                break;
            case "saved":
                state.Saved.Add(pid);
                state.Closed.Add(pid);
                break;
            case "discarded":
                state.Discarded.Add(pid);
                state.Closed.Add(pid);
                break;
            case "forced":
                state.Forced.Add(pid);
                state.Closed.Add(pid);
                break;
            default:
                state.Kept.Add(pid);
                break;
        }
    }

    private static void ReclaimResidualProcess(TrackedApp app, bool windowAlive)
    {
        if (app.TempProfile is not null)
        {
            // 窗口已关闭但进程可能仍在后台（Edge 常驻），按 exe 路径核对后再杀。
            if (app.StartedProcess && !windowAlive)
            {
                AppTermination.KillProcessTreeIfMatches(app.Pid, app.ExePath);
            }

            AppTermination.TryDeleteDirectory(app.TempProfile);
            return;
        }

        // 专用实例的窗口被用户先关掉时，回收可能残留的进程。
        bool dedicatedResidual = app.CloseWindowOnRelease &&
                                 app.DedicatedProcess &&
                                 app.StartedProcess &&
                                 !windowAlive;

        if (dedicatedResidual)
        {
            AppTermination.KillProcessTreeIfMatches(app.Pid, app.ExePath);
        }
    }

    /// <summary>释放 ref/帧/输入与显示；返回是否真正摘屏。原 finally 语义保持不变。</summary>
    private bool FinishRelease(
        ReleaseState state, string onDirty, bool forceAllClients, bool keepDisplayEffective)
    {
        bool removed = false;

        // keep_foreground 可能暂停了焦点护栏；释放时恢复，避免用户前台一直留在虚拟屏。
        _apps.ResumeFocusGuard();

        // abort 的语义是“保留未保存窗口”，因此必须保留跟踪信息。
        if (onDirty != "abort")
        {
            _apps.Apps.RemoveAll(app => forceAllClients || _apps.IsOwned(app));
        }

        // 注意：这里已持 _gate；不要再进 _refGate（Ref 路径的锁顺序是 _refGate -> _gate）。
        _display.ClearElementRefs(forceAllClients);
        _display.ClearFrames(forceAllClients);
        _display.ClearInputBlock(forceAllClients);

        if (forceAllClients)
        {
            _display.MarkDisplaySelfHealedFalse();
        }

        if (keepDisplayEffective)
        {
            _display.MarkDisplayInitialized(_display.DisplayAttached());
            _state.SaveState();
        }
        else
        {
            removed = _display.TryRemoveDisplay();
            _display.StopAndDisposeVdd();

            _display.MarkDisplayInitialized(false);
            _state.ClearStateStore();
        }

        _session.Log.Info(
            $"环境已释放 (displayRemoved={removed}, kept={keepDisplayEffective})");
        return removed;
    }

    private static ReleaseReport BuildReport(
        ReleaseState state, bool removed, bool keepDisplayEffective, string onDirty, MemoryView memory)
    {
        var closedSet = state.Closed.ToHashSet();

        return new ReleaseReport(
            Released: true,
            DisplayRemoved: removed,
            DisplayKept: keepDisplayEffective,
            OnDirty: onDirty,
            RestoredPids: state.Restored.Distinct().ToArray(),
            ClosedPids: state.Closed.Distinct().ToArray(),
            ForcedPids: state.Forced.Distinct().ToArray(),
            KeptPids: state.Kept.Where(pid => !closedSet.Contains(pid)).Distinct().ToArray(),
            SavedPids: state.Saved.Distinct().ToArray(),
            DiscardedPids: state.Discarded.Distinct().ToArray(),
            Memory: memory,
            Warnings: state.Warnings.Select(item => item!.GetValue<string>()).ToArray());
    }
}
