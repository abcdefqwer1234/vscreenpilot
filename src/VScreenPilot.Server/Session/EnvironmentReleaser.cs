using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>release_environment 所需的最小宿主接口。</summary>
internal interface IEnvironmentReleaserHost
{
    object Gate { get; }
    void IncrementEpoch();
    int ActiveClientCount { get; }
    string CurrentClientId { get; }
    TrackedAppRegistry Apps { get; }
    bool IsOwned(TrackedApp app);
    string CloseTrackedApp(TrackedApp app, string policy, out string? warning);
    void ResumeFocusGuard();
    void ClearElementRefs(bool allClients);
    void ClearFrames(bool allClients);
    void ClearInputBlock(bool allClients);
    void MarkDisplaySelfHealedFalse();
    void MarkDisplayInitialized(bool initialized);
    bool DisplayAttached { get; }
    bool TryRemoveDisplay();
    void StopAndDisposeVdd();
    void ClearStateStore();
    void SaveState();
    MemoryView TrimIdleMemory(string reason);
    LogRing Log { get; }
}

/// <summary>release_environment：按策略关闭/归位应用，释放 ref、帧、输入与虚拟屏。</summary>
internal sealed class EnvironmentReleaser
{
    private readonly IEnvironmentReleaserHost _host;

    public EnvironmentReleaser(IEnvironmentReleaserHost host) => _host = host;

    public ReleaseReport Release(
        bool keepDisplay, string onDirty = "abort", bool allowDataLoss = false,
        bool forceRemoveDisplay = false, bool forceAllClients = false)
    {
        lock (_host.Gate)
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
            _host.IncrementEpoch();

            // abort 的语义是“保留未保存窗口”。如果同时摘掉虚拟屏，窗口会被留在
            // 已不存在的显示器坐标上，因此这里强制保留屏，并明确告知调用方。
            bool keepDisplayEffective = keepDisplay;

            if (onDirty == "abort" && !keepDisplayEffective)
            {
                keepDisplayEffective = true;
                warnings.Add("on_dirty=abort 需要保留虚拟屏，已忽略 keep_display=false");
            }

            if (!keepDisplayEffective && _host.ActiveClientCount > 1 && !forceRemoveDisplay)
            {
                keepDisplayEffective = true;
                warnings.Add("其他客户端仍在使用虚拟屏，已忽略 keep_display=false");
            }

            try
            {
                IEnumerable<TrackedApp> releaseApps = forceAllClients
                    ? _host.Apps.Snapshot()
                    : _host.Apps.Where(_host.IsOwned);

                foreach (TrackedApp app in releaseApps.Reverse().ToList())
                {
                    try
                    {
                        if (app.Hwnd != IntPtr.Zero && NativeMethods.IsWindow(app.Hwnd))
                        {
                            if (app.Owned && app.CloseWindowOnRelease)
                            {
                                string outcome = _host.CloseTrackedApp(app, onDirty, out string? warning);

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
                        _host.Log.Warn($"清理应用 {app.Pid} 失败: {exception.Message}");
                        warnings.Add($"清理 {app.Pid} 失败: {exception.Message}");
                    }
                }
            }
            finally
            {
                // keep_foreground 可能暂停了焦点护栏；释放时恢复，避免用户前台一直留在虚拟屏。
                _host.ResumeFocusGuard();

                // abort 的语义是“保留未保存窗口”，因此必须保留跟踪信息：
                // 否则后续 release 无法关闭/归位这些窗口，虚拟屏上会留下工具看不见的孤儿。
                if (onDirty != "abort")
                {
                    _host.Apps.RemoveAll(app => forceAllClients || _host.IsOwned(app));
                }

                // 注意：这里已持 _gate；不要再拿 _refGate（Ref 路径的锁顺序是 _refGate -> _gate）。
                // MCP 层用 session 读写锁保证 release 不与 find/tree 并发。
                _host.ClearElementRefs(forceAllClients);

                // 基线帧是整屏 BGRA（约 3.7MB）；只清当前客户端的帧，避免 A release 清掉 B 的基线。
                _host.ClearFrames(forceAllClients);

                _host.ClearInputBlock(forceAllClients);

                if (forceAllClients)
                {
                    _host.MarkDisplaySelfHealedFalse();
                }

                if (keepDisplayEffective)
                {
                    // 会话级常驻：只归还窗口/关闭本会话应用，不摘屏，避免再次黑屏。
                    _host.MarkDisplayInitialized(_host.DisplayAttached);
                    _host.SaveState();
                }
                else
                {
                    removed = _host.TryRemoveDisplay();
                    _host.StopAndDisposeVdd();
                    
                    _host.MarkDisplayInitialized(false);
                    _host.ClearStateStore();
                }

                _host.Log.Info(
                    $"环境已释放 (displayRemoved={removed}, kept={keepDisplayEffective})");
            }

            MemoryView memory = _host.TrimIdleMemory("release");

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
}
