using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Diagnostics;
using VScreenPilot.Mcp;
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
internal interface ISnapshotHost
{
    UiaHost Uia { get; }
    ElementRefStore Refs { get; }
    string SnapshotId { get; }
    int RefCapacity { get; }
    MetricsRegistry Metrics { get; }
    DisplayInfo EnsureDisplay();
    void PruneApps();
    TrackedApp? FindApp(int? pid);
    void RequireOnDisplay(DisplayInfo display, IntPtr hwnd, string label);
    Func<bool> CancellationProbe();
    string BeginSnapshot();
    string RegisterElement(UiaElementInfo info, IntPtr hwnd);
    IReadOnlyList<TrackedApp> OwnedApps();
    bool IsLive(TrackedApp app);
    (byte[] Bgra, int Width, int Height) CaptureRegion(int x, int y, int width, int height);
    CaptureResult CaptureForSnapshot(double scale, double previewScale);
}

/// <summary>快照：环境确认、语义树、弹窗、前台、可选截图与脱敏。</summary>
internal sealed class SnapshotBuilder : IDisposable
{
    private readonly ISnapshotHost _host;
    private readonly SemaphoreSlim _snapshotGate = new(1, 1);

    public SnapshotBuilder(ISnapshotHost host) => _host = host;

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
        DisplayInfo display = _host.EnsureDisplay();
        _host.PruneApps();
        TrackedApp app = _host.FindApp(pid)
            ?? throw new ToolException(
                "E_NO_TARGET",
                pid is null
                    ? "没有已接管的应用窗口，请先 launch_or_place_app"
                    : $"pid={pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = pid });

        IntPtr hwnd = WindowManager.GetActivePopup(app.Hwnd);
        _host.RequireOnDisplay(display, hwnd, "snapshot");

        int depth = Math.Clamp(maxDepth, 1, 10);
        int nodes = Math.Clamp(maxNodes, 1, Math.Min(1024, _host.RefCapacity));
        long started = Environment.TickCount64;

        UiaTreeResult result = _host.Uia.BuildTree(
            hwnd,
            rootRuntimeId: string.Empty,
            maxDepth: depth,
            interactiveOnly: interactiveOnly,
            maxNodes: nodes,
            timeBudgetMs: 3000,
            isCancelled: _host.CancellationProbe());

        bool emptyRetried = false;
        bool effectiveInteractiveOnly = interactiveOnly;

        // 空树在 UIA 忙/首帧未就绪时很常见。重试一次并放开 interactive_only：
        // 比直接返回“1 个节点 + 无解释”对调用方有用得多（N4）。
        if (result.Nodes <= 1)
        {
            Thread.Sleep(200);

            UiaTreeResult retry = _host.Uia.BuildTree(
                hwnd,
                rootRuntimeId: string.Empty,
                maxDepth: Math.Max(depth, 4),
                interactiveOnly: false,
                maxNodes: nodes,
                timeBudgetMs: 3000,
                isCancelled: _host.CancellationProbe());

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
                new JsonObject { ["reason"] = result.Reason, ["snapshot_id"] = _host.SnapshotId });
        }

        string rootRef = string.Empty;
        JsonObject tree;
        string snapshotId;

        lock (_host.Refs.SyncRoot)
        {
            snapshotId = _host.BeginSnapshot();
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
            ["ref_capacity"] = _host.RefCapacity,
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

        _host.Metrics.RecordSnapshot(result.Nodes);

        if (maxNodes > Math.Min(1024, _host.RefCapacity))
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
                MsaaFindResult msaa = _host.Uia.InvokeMsaa(() => MsaaService.FindElements(
                    dialog.Hwnd, null, UiaNameMatch.Exact, null, 100));

                if (msaa.Items.Count > 0)
                {
                    var controls = new JsonArray();

                    lock (_host.Refs.SyncRoot)
                    {
                        foreach (UiaElementInfo info in msaa.Items)
                        {
                            var control = new JsonObject
                            {
                                ["ref"] = _host.RegisterElement(info, dialog.Hwnd),
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

        foreach (TrackedApp tracked in _host.OwnedApps())
        {
            windows.Add((JsonNode)new JsonObject
            {
                ["pid"] = tracked.Pid,
                ["hwnd"] = tracked.Hwnd.ToInt64(),
                ["title"] = _host.IsLive(tracked)
                    ? WindowManager.GetTitle(tracked.Hwnd)
                    : tracked.Title,
                ["alive"] = _host.IsLive(tracked),
                ["owned"] = tracked.Owned,
            });
        }

        data["windows"] = windows;

        IntPtr foreground = WindowManager.GetForegroundWindow();
        WindowInfo? foregroundInfo = WindowManager.FromHandle(foreground);
        bool foregroundTracked = false;

        if (foregroundInfo is not null)
        {
            foregroundTracked = _host.OwnedApps().Any(item => item.Pid == foregroundInfo.Pid);
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
                    _host.CaptureRegion(display.X, display.Y, display.Width, display.Height);

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
                CaptureResult capture = _host.CaptureForSnapshot(
                    Math.Clamp(scale, 0.1, 1.0),
                    Math.Max(Limits.MinScale, scale * Limits.PreviewScale));

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

        return new SnapshotResult(ToView(data), png, previewPng, previewMeta);
    }

    /// <summary>把组装完成的 JsonObject 收敛成强类型视图；子树保持原样。</summary>
    private static SnapshotView ToView(JsonObject data)
    {
        JsonObject window = (JsonObject)data["window"]!;
        JsonNode? dialogs = data["dialogs"];
        JsonNode? windows = data["windows"];
        JsonNode? foreground = data["foreground"];
        JsonNode? annotations = data["annotations"];
        JsonNode? screenshot = data["screenshot"];
        JsonNode? preview = data["screenshot_preview"];
        string? emptyReason = data["empty_reason"]?.GetValue<string>();

        return new SnapshotView(
            data["snapshot_id"]!.GetValue<string>(),
            data["root_ref"]!.GetValue<string>(),
            (JsonObject)data["tree"]!,
            data["text"]!.GetValue<string>(),
            data["nodes"]!.GetValue<int>(),
            data["visited"]!.GetValue<int>(),
            data["pruned"]!.GetValue<int>(),
            data["truncated"]!.GetValue<bool>(),
            data["interactive_only"]!.GetValue<bool>(),
            data["max_depth"]!.GetValue<int>(),
            data["max_nodes_effective"]!.GetValue<int>(),
            data["ref_capacity"]!.GetValue<int>(),
            data["elapsed_ms"]!.GetValue<double>(),
            new SnapshotWindowView(
                window["hwnd"]!.GetValue<long>(),
                window["pid"]!.GetValue<int>(),
                window["title"]!.GetValue<string>(),
                window["exe"] is JsonValue exe ? exe.GetValue<string>() : null),
            data["privacy_redactions"]?.GetValue<int>(),
            data["empty_retry"]?.GetValue<bool>(),
            emptyReason,
            data["max_nodes_clamped"]?.GetValue<bool>(),
            data["truncated_reason"]?.GetValue<string>(),
            dialogs,
            data["root_hwnd"]?.GetValue<long>(),
            data["root_is_dialog"]?.GetValue<bool>(),
            windows,
            foreground,
            annotations,
            screenshot,
            ScreenshotPath: null,
            ScreenshotUri: null,
            ScreenshotResourceUri: null,
            ScreenshotError: null,
            ScreenshotPreview: preview);
    }

    internal JsonObject ConvertTreeNode(UiaTreeNode node, IntPtr hwnd, ref string rootRef)
    {
        UiaElementInfo info = node.Info;
        string reference = _host.RegisterElement(info, hwnd);

        if (string.IsNullOrEmpty(rootRef))
        {
            rootRef = reference;
        }

        var json = new JsonObject
        {
            ["ref"] = reference,
            ["control_type"] = info.ControlTypeName,
        };

        if (!string.IsNullOrEmpty(info.Name))
        {
            json["name"] = info.Name.Length > 200 ? info.Name[..200] : info.Name;
        }

        if (!string.IsNullOrEmpty(info.AutomationId))
        {
            json["automation_id"] = info.AutomationId;
        }

        if (!info.Password && !string.IsNullOrEmpty(info.Value))
        {
            json["value"] = info.Value.Length > 200 ? info.Value[..200] : info.Value;
        }

        json["bounds"] = new JsonObject
        {
            ["x"] = info.Left,
            ["y"] = info.Top,
            ["width"] = info.Width,
            ["height"] = info.Height,
        };

        if (!info.Enabled)
        {
            json["enabled"] = false;
        }

        if (info.Offscreen)
        {
            json["offscreen"] = true;
        }

        if (info.Focused)
        {
            json["focused"] = true;
        }

        if (node.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (UiaTreeNode child in node.Children)
            {
                string unused = string.Empty;
                children.Add(ConvertTreeNode(child, hwnd, ref unused));
            }

            json["children"] = children;
        }

        return json;
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
