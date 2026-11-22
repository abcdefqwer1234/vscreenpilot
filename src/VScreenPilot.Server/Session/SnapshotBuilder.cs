using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Ui;
using VScreenPilot.Perception;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

internal sealed record SnapshotResult(
    SnapshotView Data, byte[]? Png, byte[]? PreviewPng, JsonObject? PreviewMeta);

/// <summary>快照构建所需的最小宿主接口；由 <see cref="SessionManager"/> 实现。</summary>
internal sealed record SnapshotStateContext(
    UiaHost Uia,
    ElementRefStore Refs,
    Func<string> SnapshotId,
    Func<int> RefCapacity,
    MetricsRegistry Metrics,
    Func<string> BeginSnapshot,
    Func<UiaElementInfo, IntPtr, string> RegisterElement);

internal sealed record SnapshotAppContext(
    Func<DisplayInfo> EnsureDisplay,
    Action PruneApps,
    Func<int?, TrackedApp?> FindApp,
    Action<DisplayInfo, IntPtr, string> RequireOnDisplay,
    Func<IReadOnlyList<TrackedApp>> OwnedApps,
    Func<TrackedApp, bool> IsLive);

internal sealed record SnapshotCaptureContext(
    Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> CaptureRegion,
    Func<double, double, CaptureResult> CaptureForSnapshot,
    Func<Func<bool>> CancellationProbe);

/// <summary>快照：环境确认、语义树、弹窗、前台、可选截图与脱敏。</summary>
internal sealed partial class SnapshotBuilder : IDisposable
{
    private readonly SnapshotStateContext _state;
    private readonly SnapshotAppContext _apps;
    private readonly SnapshotCaptureContext _capture;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);

    public SnapshotBuilder(SnapshotStateContext state, SnapshotAppContext apps, SnapshotCaptureContext capture)
    {
        _state = state;
        _apps = apps;
        _capture = capture;
    }

    public void Dispose() => _snapshotGate.Dispose();

    /// <summary>
    /// 一次调用完成“环境 + 语义树 + 弹窗 + 前台 + 可选截图”。
    /// 相比 get_environment + get_ui_tree + find_element 的三次往返，token 和延迟都更低。
    /// </summary>
    public SnapshotResult Snapshot(
        int? pid,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        bool includeValues,
        bool includeScreenshot,
        double scale,
        bool annotate = false)
    {
        // snapshot 之间串行：保证 generation 提交顺序与调用顺序一致。
        // 注意这里不持 _refGate——建树（最多 3s）和截图都不能阻塞 ref 解析。
        // 必须带超时：卡死的 UIA provider 可能让上一个 snapshot 永不返回。
        if (!_snapshotGate.Wait(TimeSpan.FromSeconds(5)))
        {
            throw new ToolException(
                "E_BUSY",
                "上一个 snapshot 仍在执行，等待 5s 未释放",
                new JsonObject { ["retry_after_ms"] = 500 },
                retryable: true,
                hint: "稍后重试；若持续发生，先用 get_log 检查 UIA provider 是否卡死",
                stage: "snapshot");
        }

        try
        {
            return SnapshotCore(
                pid, maxDepth, interactiveOnly, maxNodes, includeValues, includeScreenshot, scale, annotate);
        }
        finally
        {
            _snapshotGate.Release();
        }
    }

    private SnapshotResult SnapshotCore(
        int? pid,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        bool includeValues,
        bool includeScreenshot,
        double scale,
        bool annotate)
    {
        DisplayInfo display = _apps.EnsureDisplay();
        _apps.PruneApps();
        TrackedApp app = _apps.FindApp(pid)
            ?? throw new ToolException(
                "E_NO_TARGET",
                pid is null
                    ? "没有已接管的应用窗口，请先 launch_or_place_app"
                    : $"pid={pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = pid });

        IntPtr hwnd = WindowManager.GetActivePopup(app.Hwnd);
        _apps.RequireOnDisplay(display, hwnd, "snapshot");

        int depth = Math.Clamp(maxDepth, 1, 10);
        int nodes = Math.Clamp(maxNodes, 1, Math.Min(1024, _state.RefCapacity()));
        long started = Environment.TickCount64;

        UiaTreeResult result = BuildTree(
            hwnd, depth, nodes, interactiveOnly, out bool emptyRetried, out bool effectiveInteractiveOnly);

        RequireRoot(result);

        string rootRef = string.Empty;
        JsonObject tree;
        string snapshotId;

        lock (_state.Refs.SyncRoot)
        {
            snapshotId = _state.BeginSnapshot();
            tree = ConvertTreeNode(result.Root!, hwnd, ref rootRef);
        }

        string text = SnapshotTextFormatter.Render(tree, includeValues);
        int redactions = 0;
        text = PrivacyRedactor.RedactEmails(text, ref redactions);

        var data = new JsonObject
        {
            ["snapshot_id"] = snapshotId,
            ["root_ref"] = rootRef,
            ["tree"] = tree,
            ["text"] = text,
            ["nodes"] = result.Nodes,
            ["visited"] = result.Visited,
            ["pruned"] = result.Pruned,
            ["truncated"] = result.Truncated,
            ["interactive_only"] = effectiveInteractiveOnly,
            ["max_depth"] = depth,
            ["max_nodes_effective"] = nodes,
            ["ref_capacity"] = _state.RefCapacity(),
            ["elapsed_ms"] = Math.Round((double)(Environment.TickCount64 - started), 1),
            ["window"] = new JsonObject
            {
                ["hwnd"] = hwnd.ToInt64(),
                ["pid"] = app.Pid,
                ["title"] = PrivacyRedactor.RedactEmails(WindowManager.GetTitle(hwnd), ref redactions),
                ["exe"] = app.ExePath,
            },
        };

        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }

        if (emptyRetried)
        {
            data["empty_retry"] = true;
        }

        if (result.Nodes <= 1)
        {
            data["empty_reason"] = result.Reason
                ?? (result.Visited <= 1
                    ? "root_only"
                    : result.Pruned > 0 ? "pruned_all" : "uia_busy");
        }

        _state.Metrics.RecordSnapshot(result.Nodes);

        if (maxNodes > Math.Min(1024, _state.RefCapacity()))
        {
            data["max_nodes_clamped"] = true;
        }

        if (result.Reason is not null)
        {
            data["truncated_reason"] = result.Reason;
        }

        BuildDialogs(app, hwnd, data, ref redactions);
        BuildWindowsAndForeground(data);

        (byte[]? png, byte[]? previewPng, JsonObject? previewMeta) =
            BuildScreenshot(data, tree, display, includeScreenshot, annotate, scale);

        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }

        PrivacyRedactor.RedactJsonStrings(data, ref redactions);

        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }

        return new SnapshotResult(ToView(data), png, previewPng, previewMeta);
    }

    private void RequireRoot(UiaTreeResult result)
    {
        if (result.Root is null)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                result.Reason == "root_not_found" ? "树根控件已不存在" : "无法读取语义树",
                new JsonObject { ["reason"] = result.Reason, ["snapshot_id"] = _state.SnapshotId() });
        }
    }


    private UiaTreeResult BuildTree(
        IntPtr hwnd, int depth, int nodes, bool interactiveOnly,
        out bool emptyRetried, out bool effectiveInteractiveOnly)
    {
        UiaTreeResult result = _state.Uia.BuildTree(
            hwnd,
            rootRuntimeId: string.Empty,
            maxDepth: depth,
            interactiveOnly: interactiveOnly,
            maxNodes: nodes,
            timeBudgetMs: 3000,
            isCancelled: _capture.CancellationProbe());

        emptyRetried = false;
        effectiveInteractiveOnly = interactiveOnly;

        // 空树在 UIA 首帧未就绪时很常见。重试一次并放开 interactive_only；
        // 比直接返回“1 个节点 + 无解释”对调用方有用得多（N4）。
        if (result.Nodes <= 1)
        {
            Thread.Sleep(200);

            UiaTreeResult retry = _state.Uia.BuildTree(
                hwnd,
                rootRuntimeId: string.Empty,
                maxDepth: Math.Max(depth, 4),
                interactiveOnly: false,
                maxNodes: nodes,
                timeBudgetMs: 3000,
                isCancelled: _capture.CancellationProbe());

            if (retry.Root is not null && retry.Nodes > result.Nodes)
            {
                result = retry;
                emptyRetried = true;
                effectiveInteractiveOnly = false;
            }
        }

        return result;
    }

    private void BuildDialogs(TrackedApp app, IntPtr hwnd, JsonObject data, ref int redactions)
    {
        var dialogs = new JsonArray();
        bool rootListed = false;

        foreach (WindowInfo dialog in WindowManager.ListOwnedWindows(app.Pid, hwnd))
        {
            if (dialog.Hwnd == hwnd)
            {
                rootListed = true;
            }

            var dialogJson = new JsonObject
            {
                ["hwnd"] = dialog.Hwnd.ToInt64(),
                ["title"] = PrivacyRedactor.RedactEmails(dialog.Title, ref redactions),
                ["class"] = dialog.ClassName,
                ["bounds"] = new JsonObject
                {
                    ["x"] = dialog.Rect.Left,
                    ["y"] = dialog.Rect.Top,
                    ["width"] = dialog.Width,
                    ["height"] = dialog.Height,
                },
            };

            // Office 旧对话框（bosa_sdm_msword）UIA 树为空，MSAA 是唯一语义来源。
            // 把控件直接放进 snapshot.dialogs[].controls，模型不需要再调 find_element。
            if (dialog.ClassName.Contains("bosa", StringComparison.OrdinalIgnoreCase))
            {
                MsaaFindResult msaa = _state.Uia.InvokeMsaa(() => MsaaService.FindElements(
                    dialog.Hwnd, null, UiaNameMatch.Exact, null, 100));

                if (msaa.Items.Count > 0)
                {
                    var controls = new JsonArray();

                    lock (_state.Refs.SyncRoot)
                    {
                        foreach (UiaElementInfo info in msaa.Items)
                        {
                            var control = new JsonObject
                            {
                                ["ref"] = _state.RegisterElement(info, dialog.Hwnd),
                                ["name"] = info.Name,
                                ["control_type"] = info.ControlTypeName,
                                ["bounds"] = new JsonObject
                                {
                                    ["x"] = info.Left,
                                    ["y"] = info.Top,
                                    ["width"] = info.Width,
                                    ["height"] = info.Height,
                                },
                            };

                            if (!string.IsNullOrEmpty(info.Value))
                            {
                                control["value"] = info.Value;
                            }

                            controls.Add(control);
                        }
                    }

                    dialogJson["controls"] = controls;
                    dialogJson["msaa_truncated"] = msaa.Truncated;
                }
            }

            dialogs.Add(dialogJson);
        }

        // snapshot 的 root 可能已经是弹窗（GetActivePopup）。此时 dialogs[] 不含它，
        // 调用方会看到“root 是对话框、dialogs 却是空”的矛盾（N6）。
        if (hwnd != app.Hwnd)
        {
            data["root_hwnd"] = hwnd.ToInt64();
            data["root_is_dialog"] = true;

            if (!rootListed)
            {
                WindowInfo info = WindowManager.FromHandle(hwnd)
                    ?? new WindowInfo(
                        hwnd, app.Pid, string.Empty, string.Empty,
                        default, Visible: false, Owned: false);

                dialogs.Insert(0, (JsonNode)new JsonObject
                {
                    ["hwnd"] = hwnd.ToInt64(),
                    ["title"] = PrivacyRedactor.RedactEmails(info.Title, ref redactions),
                    ["class"] = info.ClassName,
                    ["is_root"] = true,
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = info.Rect.Left,
                        ["y"] = info.Rect.Top,
                        ["width"] = info.Width,
                        ["height"] = info.Height,
                    },
                });
            }
        }

        data["dialogs"] = dialogs;
    }

    private void BuildWindowsAndForeground(JsonObject data)
    {
        var windows = new JsonArray();

        foreach (TrackedApp tracked in _apps.OwnedApps())
        {
            windows.Add((JsonNode)new JsonObject
            {
                ["pid"] = tracked.Pid,
                ["hwnd"] = tracked.Hwnd.ToInt64(),
                ["title"] = _apps.IsLive(tracked)
                    ? WindowManager.GetTitle(tracked.Hwnd)
                    : tracked.Title,
                ["alive"] = _apps.IsLive(tracked),
                ["owned"] = tracked.Owned,
            });
        }

        data["windows"] = windows;

        IntPtr foreground = WindowManager.GetForegroundWindow();
        WindowInfo? foregroundInfo = WindowManager.FromHandle(foreground);
        bool foregroundTracked = false;

        if (foregroundInfo is not null)
        {
            foregroundTracked = _apps.OwnedApps().Any(item => item.Pid == foregroundInfo.Pid);
        }

        data["foreground"] = foregroundInfo is null
            ? null
            : new JsonObject
            {
                ["hwnd"] = foregroundInfo.Hwnd.ToInt64(),
                ["pid"] = foregroundInfo.Pid,
                ["title"] = foregroundTracked ? foregroundInfo.Title : null,
                ["tracked"] = foregroundTracked,
            };
    }

}
