using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Diagnostics;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Perception;
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
        DisplayInfo display = EnsureEnvironmentFast();
        PruneDeadApps();
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
            isCancelled: CancellationProbe());

        bool emptyRetried = false;
        bool effectiveInteractiveOnly = interactiveOnly;

        // 空树在 UIA 忙/首帧未就绪时很常见。重试一次并放开 interactive_only：
        // 比直接返回“1 个节点 + 无解释”对调用方有用得多（N4）。
        if (result.Nodes <= 1)
        {
            Thread.Sleep(200);

            UiaTreeResult retry = Uia.BuildTree(
                hwnd,
                rootRuntimeId: string.Empty,
                maxDepth: Math.Max(depth, 4),
                interactiveOnly: false,
                maxNodes: nodes,
                timeBudgetMs: 3000,
                isCancelled: CancellationProbe());

            if (retry.Root is not null && retry.Nodes > result.Nodes)
            {
                result = retry;
                emptyRetried = true;
                effectiveInteractiveOnly = false;
            }
        }

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

        lock (_refs.SyncRoot)
        {
            snapshotId = BeginSnapshot();
            tree = ConvertTreeNode(result.Root, hwnd, ref rootRef);
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
            ["ref_capacity"] = ElementRefCapacity,
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

        MetricsRegistry.RecordSnapshot(result.Nodes);

        if (maxNodes > Math.Min(1024, ElementRefCapacity))
        {
            data["max_nodes_clamped"] = true;
        }

        if (result.Reason is not null)
        {
            data["truncated_reason"] = result.Reason;
        }

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
            // 把控件直接放进 snapshot.dialogs[].controls，模型不需要再猜 find_element。
            if (dialog.ClassName.Contains("bosa", StringComparison.OrdinalIgnoreCase))
            {
                MsaaFindResult msaa = MsaaService.FindElements(
                    dialog.Hwnd, null, UiaNameMatch.Exact, null, 100);

                if (msaa.Items.Count > 0)
                {
                    var controls = new JsonArray();

                    lock (_refs.SyncRoot)
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

        // snapshot 的 root 可能已经是弹窗（GetActivePopup）。此前 dialogs[] 不含它，
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

        var windows = new JsonArray();

        lock (_gate)
        {
            foreach (TrackedApp tracked in _apps.Where(IsOwned))
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
                foregroundTracked = _apps.Any(item => IsOwned(item) && item.Pid == foregroundInfo.Pid);
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
            if (annotate)
            {
                var annotations = new JsonArray();
                CollectAnnotations(tree, display, annotations, maxCount: 80);
                data["annotations"] = annotations;

                // 直接用源帧做叠加，再缩放编码；网格让模型估坐标时有参照。
                (byte[] bgra, int captureWidth, int captureHeight) =
                    CaptureRegion(display.X, display.Y, display.Width, display.Height);

                double effectiveScale = Math.Clamp(scale, 0.1, 1.0);
                int thickness = Math.Max(2, (int)Math.Ceiling(2 / effectiveScale));
                int numberScale = Math.Max(2, (int)Math.Ceiling(2 / effectiveScale));

                PngOverlay.DrawGrid(bgra, captureWidth, captureHeight, 100);

                foreach (JsonNode? item in annotations)
                {
                    if (item is not JsonObject annotation ||
                        annotation["bounds"] is not JsonObject bounds)
                    {
                        continue;
                    }

                    int index = annotation["index"]?.GetValue<int>() ?? 0;
                    int bx = (bounds["x"]?.GetValue<int>() ?? 0) - display.X;
                    int by = (bounds["y"]?.GetValue<int>() ?? 0) - display.Y;
                    int bw = bounds["width"]?.GetValue<int>() ?? 0;
                    int bh = bounds["height"]?.GetValue<int>() ?? 0;
                    (byte r, byte g, byte b) = PngOverlay.ColorFor(Math.Max(0, index - 1));

                    PngOverlay.DrawBox(
                        bgra, captureWidth, captureHeight, bx, by, bw, bh, r, g, b, thickness);
                    PngOverlay.DrawNumber(
                        bgra, captureWidth, captureHeight, bx + 2, by + 2, index, numberScale);
                }

                png = PngWriter.Encode(
                    bgra, captureWidth, captureHeight, effectiveScale,
                    out int outputWidth, out int outputHeight);
                previewPng = PngWriter.Encode(
                    bgra, captureWidth, captureHeight, Math.Max(Limits.MinScale, effectiveScale * Limits.PreviewScale),
                    out int previewWidth, out int previewHeight);

                data["screenshot"] = new JsonObject
                {
                    ["x"] = display.X,
                    ["y"] = display.Y,
                    ["width"] = outputWidth,
                    ["height"] = outputHeight,
                    ["source_width"] = captureWidth,
                    ["source_height"] = captureHeight,
                    ["scale"] = effectiveScale,
                    ["png_bytes"] = png.Length,
                    ["backend"] = "gdi+overlay",
                    ["baseline"] = new JsonObject
                    {
                        ["x"] = display.X,
                        ["y"] = display.Y,
                        ["width"] = captureWidth,
                        ["height"] = captureHeight,
                    },
                };
                previewMeta = new JsonObject
                {
                    ["width"] = previewWidth,
                    ["height"] = previewHeight,
                    ["scale"] = Math.Max(Limits.MinScale, effectiveScale * Limits.PreviewScale),
                    ["png_bytes"] = previewPng.Length,
                };
            }
            else
            {
                // 快照截图不写帧缓存；preview 与主图同帧，且不阻塞 _refGate（已在锁外）。
                CaptureResult capture = CaptureScreenEx(
                    null, null, null, null, Math.Clamp(scale, 0.1, 1.0), savePath: null,
                    storeFrame: false, previewScale: Math.Max(Limits.MinScale, scale * Limits.PreviewScale));

                png = capture.Png;
                previewPng = capture.PreviewPng;
                previewMeta = capture.PreviewMeta is null
                    ? null
                    : VspJson.ToNode(capture.PreviewMeta).AsObject();
                data["screenshot"] = VspJson.ToNode(capture.Meta);
            }
        }

        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }

        PrivacyRedactor.RedactJsonStrings(data, ref redactions);

        if (redactions > 0)
        {
            data["privacy_redactions"] = redactions;
        }

        return new SnapshotResult(data, png, previewPng, previewMeta);
    }

    private static void CollectAnnotations(
        JsonObject node, DisplayInfo display, JsonArray target, int maxCount)
    {
        if (target.Count < maxCount && node["bounds"] is JsonObject bounds)
        {
            int x = bounds["x"]?.GetValue<int>() ?? 0;
            int y = bounds["y"]?.GetValue<int>() ?? 0;
            int width = bounds["width"]?.GetValue<int>() ?? 0;
            int height = bounds["height"]?.GetValue<int>() ?? 0;
            string? name = node["name"]?.GetValue<string>();
            string controlType = node["control_type"]?.GetValue<string>() ?? string.Empty;
            bool interesting = !string.IsNullOrWhiteSpace(name) ||
                               controlType is "Button" or "Edit" or "Document" or "ListItem"
                                   or "MenuItem" or "TabItem" or "ComboBox";

            bool offscreen = node["offscreen"]?.GetValue<bool>() == true;

            if (!offscreen &&
                interesting &&
                width >= 8 && height >= 8 &&
                x + width > display.X && y + height > display.Y &&
                x < display.X + display.Width && y < display.Y + display.Height)
            {
                var annotation = new JsonObject
                {
                    ["index"] = target.Count + 1,
                    ["ref"] = node["ref"]?.GetValue<string>(),
                    ["control_type"] = controlType,
                    ["bounds"] = bounds.DeepClone(),
                };

                if (!string.IsNullOrWhiteSpace(name))
                {
                    annotation["name"] = name.Length > 80 ? name[..80] : name;
                }

                target.Add(annotation);
            }
        }

        if (node["children"] is JsonArray children)
        {
            foreach (JsonNode? child in children)
            {
                if (child is JsonObject childObject)
                {
                    CollectAnnotations(childObject, display, target, maxCount);
                }
            }
        }
    }
}
