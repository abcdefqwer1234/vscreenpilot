using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Core.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

internal sealed record SnapshotResult(
    JsonObject Data, byte[]? Png, byte[]? PreviewPng, JsonObject? PreviewMeta);

internal sealed partial class SessionManager
{
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
        double scale)
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
                pid, maxDepth, interactiveOnly, maxNodes, includeValues, includeScreenshot, scale);
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
        double scale)
    {
        DisplayInfo display = EnsureEnvironmentFast();
        TrackedApp app = FindLiveTrackedApp(pid)
            ?? throw new ToolException(
                "E_NO_TARGET",
                pid is null
                    ? "没有已接管的应用窗口，请先 launch_or_place_app"
                    : $"pid={pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = pid });

        IntPtr hwnd = WindowManager.GetActivePopup(app.Hwnd);
        RequireWindowOnDisplay(display, hwnd, "snapshot");

        int depth = Math.Clamp(maxDepth, 1, 10);
        int nodes = Math.Clamp(maxNodes, 1, Math.Min(1024, ElementRefCapacity));
        long started = Environment.TickCount64;

        UiaTreeResult result = Uia.BuildTree(
            hwnd,
            rootRuntimeId: string.Empty,
            maxDepth: depth,
            interactiveOnly: interactiveOnly,
            maxNodes: nodes,
            timeBudgetMs: 3000,
            isCancelled: () => Cancellation.IsCancellationRequested);

        if (result.Root is null)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                result.Reason == "root_not_found" ? "树根控件已不存在" : "无法读取语义树",
                new JsonObject { ["reason"] = result.Reason, ["snapshot_id"] = SnapshotId });
        }

        string rootRef = string.Empty;
        JsonObject tree;
        string snapshotId;

        lock (_refGate)
        {
            snapshotId = BeginSnapshot();
            tree = ConvertTreeNode(result.Root, hwnd, ref rootRef);
        }

        string text = SnapshotFormat.Render(tree, includeValues);

        var data = new JsonObject
        {
            ["snapshot_id"] = snapshotId,
            ["root_ref"] = rootRef,
            ["tree"] = tree,
            ["text"] = text,
            ["nodes"] = result.Nodes,
            ["truncated"] = result.Truncated,
            ["interactive_only"] = interactiveOnly,
            ["max_depth"] = depth,
            ["max_nodes_effective"] = nodes,
            ["ref_capacity"] = ElementRefCapacity,
            ["elapsed_ms"] = Math.Round((double)(Environment.TickCount64 - started), 1),
            ["window"] = new JsonObject
            {
                ["hwnd"] = hwnd.ToInt64(),
                ["pid"] = app.Pid,
                ["title"] = WindowManager.GetTitle(hwnd),
                ["exe"] = app.ExePath,
            },
        };

        if (maxNodes > Math.Min(1024, ElementRefCapacity))
        {
            data["max_nodes_clamped"] = true;
        }

        if (result.Reason is not null)
        {
            data["truncated_reason"] = result.Reason;
        }

        var dialogs = new JsonArray();

        foreach (WindowInfo dialog in WindowManager.ListOwnedWindows(app.Pid, hwnd))
        {
            var dialogJson = new JsonObject
            {
                ["hwnd"] = dialog.Hwnd.ToInt64(),
                ["title"] = dialog.Title,
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
            // 把控件直接放进 snapshot.dialogs[].controls，模型不需要再猜 find_element。
            if (dialog.ClassName.Contains("bosa", StringComparison.OrdinalIgnoreCase))
            {
                MsaaFindResult msaa = MsaaService.FindElements(
                    dialog.Hwnd, null, UiaNameMatch.Exact, null, 100);

                if (msaa.Items.Count > 0)
                {
                    var controls = new JsonArray();

                    lock (_refGate)
                    {
                        foreach (UiaElementInfo info in msaa.Items)
                        {
                            var control = new JsonObject
                            {
                                ["ref"] = RegisterElement(info, dialog.Hwnd),
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

        data["dialogs"] = dialogs;

        var windows = new JsonArray();

        lock (_gate)
        {
            foreach (TrackedApp tracked in _apps)
            {
                windows.Add((JsonNode)new JsonObject
                {
                    ["pid"] = tracked.Pid,
                    ["hwnd"] = tracked.Hwnd.ToInt64(),
                    ["title"] = IsLiveTrackedApp(tracked)
                        ? WindowManager.GetTitle(tracked.Hwnd)
                        : tracked.Title,
                    ["alive"] = IsLiveTrackedApp(tracked),
                    ["owned"] = tracked.Owned,
                });
            }
        }

        data["windows"] = windows;

        IntPtr foreground = WindowManager.GetForegroundWindow();
        WindowInfo? foregroundInfo = WindowManager.FromHandle(foreground);
        bool foregroundTracked = false;

        if (foregroundInfo is not null)
        {
            lock (_gate)
            {
                foregroundTracked = _apps.Any(item => item.Pid == foregroundInfo.Pid);
            }
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

        byte[]? png = null;
        byte[]? previewPng = null;
        JsonObject? previewMeta = null;

        if (includeScreenshot)
        {
            // 快照截图不写帧缓存；preview 与主图同帧，且不阻塞 _refGate（已在锁外）。
            CaptureResult capture = CaptureScreenEx(
                null, null, null, null, Math.Clamp(scale, 0.1, 1.0), savePath: null,
                storeFrame: false, previewScale: Math.Max(0.1, scale * 0.25));

            png = capture.Png;
            previewPng = capture.PreviewPng;
            previewMeta = capture.PreviewMeta;
            data["screenshot"] = capture.Meta;
        }

        return new SnapshotResult(data, png, previewPng, previewMeta);
    }
}
