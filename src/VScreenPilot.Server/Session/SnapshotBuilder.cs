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
internal sealed class SnapshotBuilder : IDisposable
{
    internal readonly SnapshotStateContext _state;
    internal readonly SnapshotAppContext _apps;
    internal readonly SnapshotCaptureContext _capture;
    internal readonly SemaphoreSlim _snapshotGate = new(1, 1);

    internal readonly SnapshotSectionOps Sections;
    internal readonly SnapshotConvertOps ConvertOps;
    internal readonly SnapshotScreenshotOps ScreenshotOps;

    internal JsonObject ConvertTreeNode(UiaTreeNode node, IntPtr hwnd, ref string rootRef) =>
        ConvertOps.ConvertTreeNode(node, hwnd, ref rootRef);

    public SnapshotBuilder(SnapshotStateContext state, SnapshotAppContext apps, SnapshotCaptureContext capture)
    {
        Sections = new SnapshotSectionOps(this);
        ConvertOps = new SnapshotConvertOps(this);
        ScreenshotOps = new SnapshotScreenshotOps(this);
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

        UiaTreeResult result = Sections.BuildTree(
            hwnd, depth, nodes, interactiveOnly, out bool emptyRetried, out bool effectiveInteractiveOnly);

        Sections.RequireRoot(result);

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

        var context = new SnapshotBuildContext(
            display, app, hwnd, depth, nodes, result, snapshotId, rootRef, tree, text,
            started, effectiveInteractiveOnly, emptyRetried);
        JsonObject data = CreateSnapshotData(context, ref redactions);

        Sections.BuildDialogs(app, hwnd, data, ref redactions);
        Sections.BuildWindowsAndForeground(data);

        (byte[]? png, byte[]? previewPng, JsonObject? previewMeta) =
            ScreenshotOps.BuildScreenshot(data, tree, display, includeScreenshot, annotate, scale);

        ApplyRedactions(data, ref redactions);

        return new SnapshotResult(ConvertOps.ToView(data), png, previewPng, previewMeta);
    }

    private sealed record SnapshotBuildContext(
        DisplayInfo Display,
        TrackedApp App,
        IntPtr Hwnd,
        int Depth,
        int Nodes,
        UiaTreeResult Result,
        string SnapshotId,
        string RootRef,
        JsonObject Tree,
        string Text,
        long Started,
        bool EffectiveInteractiveOnly,
        bool EmptyRetried);

    private JsonObject CreateSnapshotData(SnapshotBuildContext context, ref int redactions)
    {
        var data = new JsonObject
        {
            ["snapshot_id"] = context.SnapshotId,
            ["root_ref"] = context.RootRef,
            ["tree"] = context.Tree,
            ["text"] = context.Text,
            ["nodes"] = context.Result.Nodes,
            ["visited"] = context.Result.Visited,
            ["pruned"] = context.Result.Pruned,
            ["truncated"] = context.Result.Truncated,
            ["interactive_only"] = context.EffectiveInteractiveOnly,
            ["max_depth"] = context.Depth,
            ["max_nodes_effective"] = context.Nodes,
            ["ref_capacity"] = _state.RefCapacity(),
            ["elapsed_ms"] = Math.Round((double)(Environment.TickCount64 - context.Started), 1),
            ["window"] = new JsonObject
            {
                ["hwnd"] = context.Hwnd.ToInt64(),
                ["pid"] = context.App.Pid,
                ["title"] = PrivacyRedactor.RedactEmails(
                    WindowManager.GetTitle(context.Hwnd), ref redactions),
                ["exe"] = context.App.ExePath,
            },
        };

        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }

        if (context.EmptyRetried)
        {
            data["empty_retry"] = true;
        }

        if (context.Result.Nodes <= 1)
        {
            data["empty_reason"] = context.Result.Reason
                ?? (context.Result.Visited <= 1
                    ? "root_only"
                    : context.Result.Pruned > 0 ? "pruned_all" : "uia_busy");
        }

        _state.Metrics.RecordSnapshot(context.Result.Nodes);

        if (context.Nodes > Math.Min(1024, _state.RefCapacity()))
        {
            data["max_nodes_clamped"] = true;
        }

        if (context.Result.Reason is not null)
        {
            data["truncated_reason"] = context.Result.Reason;
        }

        return data;
    }

    private static void ApplyRedactions(JsonObject data, ref int redactions)
    {
        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }

        PrivacyRedactor.RedactJsonStrings(data, ref redactions);

        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }
    }
}
