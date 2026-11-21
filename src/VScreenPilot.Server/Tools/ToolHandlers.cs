using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Perception;
using VScreenPilot.Session;
using VScreenPilot.Views;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>工具处理器：只解析参数并调用会话/领域组件，不承担调度与协议。</summary>
internal sealed partial class McpTransport
{
    private ToolResponse GetEnvironment(JsonElement arguments)
    {
        EnvironmentView environment = _session.GetEnvironment(OptionalBool(arguments, "ensure", false));
        JsonObject data = VspJson.ToNode(environment).AsObject();
        data["client_id"] = _client?.Id ?? "c0";
        data["screen_lease"] = _session.Lease.Describe();
        return ToolResponse.Text(data);
    }

    private ToolResponse LaunchOrPlace(JsonElement arguments)
    {
        string? path = OptionalString(arguments, "path");
        string? window = OptionalString(arguments, "window");
        string? args = OptionalString(arguments, "args");
        int waitMs = OptionalInt(arguments, "wait_ms", 15000);
        bool newInstance = OptionalBool(arguments, "new_instance", true);
        int? pid = OptionalNullableInt(arguments, "pid");

        return ToolResponseWriter.FromView(
            _session.LaunchOrPlaceApp(
                path, window, args, waitMs, newInstance,
                OptionalBool(arguments, "ensure_document", true), pid));
    }

    private ToolResponse Snapshot(JsonElement arguments)
    {
        double scale = OptionalDouble(arguments, "scale", 0.5);

        if (arguments.TryGetProperty("scale", out _) && (scale < 0.1 || scale > 1.0))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"scale 必须在 0.1..1.0（收到 {scale}）",
                new JsonObject { ["field"] = "scale", ["value"] = scale },
                retryable: false,
                hint: "快照截图只接受 0.1–1.0",
                stage: "validate");
        }

        bool annotate = OptionalBool(arguments, "annotate", false);

        SnapshotResult result = _session.Snapshot(
            OptionalNullableInt(arguments, "pid"),
            OptionalInt(arguments, "max_depth", 4),
            OptionalBool(arguments, "interactive_only", true),
            OptionalInt(arguments, "max_nodes", 300),
            OptionalBool(arguments, "include_values", true),
            OptionalBool(arguments, "include_screenshot", false) || annotate,
            scale,
            annotate);

        if (result.Png is null)
        {
            return ToolResponse.Text(result.Data);
        }

        if (result.Png.Length <= Limits.InlineLimitBytes)
        {
            return ToolResponse.Text(result.Data).AddImage(result.Png);
        }

        // 大图先落盘、把路径写进 data，再序列化；否则 Text() 之后追加的字段不会进入响应。
        string artifactUri;
        string artifactName;

        try
        {
            (string path, string uri) = WriteArtifact(result.Png, "snapshot", result.PreviewPng);
            artifactUri = uri;
            artifactName = Path.GetFileName(path);
            result.Data["screenshot_path"] = path;
            result.Data["screenshot_uri"] = uri;
            result.Data["screenshot_resource_uri"] = uri;
        }
        catch (Exception exception)
        {
            result.Data["screenshot_error"] = exception.Message;
            return ToolResponse.Text(result.Data);
        }

        bool hasPreview = result.PreviewPng is not null;

        if (hasPreview)
        {
            // preview 与主图来自同一帧，不再二次截屏。
            result.Data["screenshot_preview"] = result.PreviewMeta;
        }

        ToolResponse response = ToolResponse.Text(result.Data)
            .AddResourceLink(artifactUri, artifactName, "image/png", result.Png.Length);

        if (!hasPreview)
        {
            return response;
        }

        return response.AddImage(result.PreviewPng!);
    }

    private (string Path, string Uri) WriteArtifact(byte[] png, string prefix, byte[]? preview = null)
    {
        string fileName =
            $"{prefix}-{DateTime.Now:HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}.png";
        string path = Path.Combine(_session.ArtifactDirectory, fileName);
        File.WriteAllBytes(path, png);

        if (preview is not null)
        {
            // 资源读取时用 ?preview=1 指向这张小图，避免把 300KB+ 原图塞回 Blob。
            File.WriteAllBytes(Path.ChangeExtension(path, ".preview.png"), preview);
        }

        return (path, $"vsp://artifacts/{fileName}");
    }

    private ToolResponse CaptureScreen(JsonElement arguments)
    {
        int? x = null, y = null, width = null, height = null;

        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("region", out JsonElement region))
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            x = RequiredInt(region, "x");
            y = RequiredInt(region, "y");
            width = RequiredInt(region, "width");
            height = RequiredInt(region, "height");
        }

        // 全屏默认降到 0.5：UI 截图缩小一半仍然可读，体积/上下文成本降 4 倍。
        double scale = OptionalDouble(arguments, "scale", x is null ? 0.5 : 1.0);

        if (arguments.TryGetProperty("scale", out _) && (scale < 0.1 || scale > 1.0))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"scale 必须在 0.1..1.0（收到 {scale}）",
                new JsonObject { ["field"] = "scale", ["value"] = scale },
                retryable: false,
                hint: "0 或 10 这类值不会再被静默钳制；需要小图请用 0.1–1.0",
                stage: "validate");
        }
        string? savePath = OptionalString(arguments, "save_path");
        bool inline = OptionalBool(arguments, "inline", true);

        // 关键发现：全屏“悬崖”主要不是 GDI 采集，而是大 PNG 的 Base64/JSON/stdio 回传。
        // 366KB PNG 内联 → 客户端约 1.2s；落盘 + 200B 元数据 → 约 35ms。
        // 因此超过 128KB 的图默认落 artifact，并额外内联一张 0.25× 预览。
        const int InlineLimit = Limits.InlineLimitBytes;
        bool hasSavePath = !string.IsNullOrWhiteSpace(savePath);

        // preview 与主图同帧；small 图会多编码一次缩略图，但省掉了第二次截屏。
        SessionManager.CaptureResult capture = _session.CaptureScreenEx(
            x, y, width, height, scale, savePath,
            storeFrame: true,
            previewScale: inline ? Math.Max(Limits.MinScale, scale * Limits.PreviewScale) : null);

        byte[] png = capture.Png;
        JsonObject meta = VspJson.ToNode(capture.Meta).AsObject();

        if (inline && png.Length <= InlineLimit)
        {
            meta["inline"] = true;
            return ToolResponse.Text(meta).AddImage(png);
        }

        string? artifactUri = null;
        string? artifactName = null;

        // 显式 save_path 就是唯一正式产物：不再额外写 artifact（R4）。
        if (!hasSavePath)
        {
            try
            {
                (string path, string uri) = WriteArtifact(png, "shot", capture.PreviewPng);
                meta["artifact_path"] = path;
                meta["artifact_uri"] = uri;
                meta["resource_uri"] = uri;
                meta["path"] = path;
                artifactUri = uri;
                artifactName = Path.GetFileName(path);
            }
            catch (Exception exception)
            {
                meta["artifact_error"] = exception.Message;
            }
        }

        meta["inline"] = false;

        bool hasPreview = inline && capture.PreviewPng is not null;

        if (hasPreview)
        {
            meta["preview"] = VspJson.ToNode(capture.PreviewMeta);
        }

        ToolResponse response = ToolResponse.Text(meta);

        if (artifactUri is not null)
        {
            // 标准 resource_link：客户端可直接按 URI 读取大图，不必依赖自定义字段。
            response.AddResourceLink(artifactUri, artifactName ?? "shot.png", "image/png", png.Length);
        }

        if (hasPreview)
        {
            response.AddImage(capture.PreviewPng!);
        }

        return response;
    }

    private ToolResponse FindElement(JsonElement arguments)
    {
        string? name = OptionalString(arguments, "name");
        string? controlType = OptionalString(arguments, "control_type");
        string? automationId = OptionalString(arguments, "automation_id");
        if (controlType is not null && !Uia.UiaService.IsKnownControlType(controlType))
        {
            var supported = new JsonArray();

            foreach (string item in Uia.UiaService.SupportedControlTypes)
            {
                supported.Add(item);
            }

            throw new ToolException(
                "E_INVALID_ARG",
                $"control_type 无效: {controlType}",
                new JsonObject { ["field"] = "control_type", ["supported"] = supported },
                retryable: false,
                hint: "使用 UIA 控件类型英文名，例如 Button、Edit、Document",
                stage: "validate");
        }

        string nameMatch = OptionalString(arguments, "name_match") ?? "exact";
        int index = OptionalInt(arguments, "index", 0);
        bool all = OptionalBool(arguments, "all", false);
        int maxResults = OptionalInt(arguments, "max_results", 20);
        bool includeOffscreen = OptionalBool(arguments, "include_offscreen", false);
        int? pid = OptionalNullableInt(arguments, "pid");
        string? role = OptionalString(arguments, "role");
        string prefer = OptionalString(arguments, "prefer") ?? "auto";

        return ToolResponse.Text(_session.FindElement(
            name, controlType, automationId, nameMatch, index, all, maxResults,
            includeOffscreen, pid, role, prefer));
    }

    private ToolResponse GetUiTree(JsonElement arguments)
    {
        int? maxDepth = OptionalNullableInt(arguments, "max_depth");
        bool? interactiveOnly = OptionalNullableBool(arguments, "interactive_only");
        int? maxNodes = OptionalNullableInt(arguments, "max_nodes");
        string? root = OptionalString(arguments, "root");
        int? pid = OptionalNullableInt(arguments, "pid");
        string? format = OptionalString(arguments, "format");
        bool newSnapshot = OptionalBool(arguments, "new_snapshot", false);

        return ToolResponse.Text(
            _session.GetUiTree(maxDepth, interactiveOnly, maxNodes, root, pid, format, newSnapshot));
    }

    private ToolResponse InvokeElement(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "invoke_element 需要 ref");
        bool allowClick = OptionalBool(arguments, "allow_click", false);
        string? pattern = OptionalString(arguments, "pattern");

        return ToolResponseWriter.FromView(_session.InvokeElement(reference, allowClick, pattern));
    }

    private ToolResponse SetElementValue(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 ref");
        string value = OptionalString(arguments, "value")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 value");
        bool allowKeyboard = OptionalBool(arguments, "allow_keyboard", false);
        string? mode = OptionalString(arguments, "mode");
        bool allowDestructive = OptionalBool(arguments, "allow_destructive", false);

        return ToolResponse.Text(_session.SetElementValue(
            reference, value, allowKeyboard, mode, allowDestructive));
    }

    private ToolResponse GetElementText(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "get_element_text 需要 ref");
        int maxChars = OptionalInt(arguments, "max_chars", 4096);

        return ToolResponseWriter.FromView(_session.GetElementText(reference, maxChars));
    }

    private ToolResponse ReadScreenText(JsonElement arguments)
    {
        int? x = null, y = null, width = null, height = null;

        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("region", out JsonElement region))
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            x = OptionalNullableInt(region, "x");
            y = OptionalNullableInt(region, "y");
            width = OptionalNullableInt(region, "width");
            height = OptionalNullableInt(region, "height");

            if (x is null || y is null || width is null || height is null)
            {
                throw new ToolException(
                    "E_INVALID_ARG", "region 需要 x/y/width/height",
                    hint: "省略 region 表示整个虚拟屏", stage: "validate");
            }
        }

        return ToolResponse.Text(_session.ReadScreenText(
            x, y, width, height,
            OptionalString(arguments, "query"),
            OptionalInt(arguments, "max_lines", 50)));
    }

    private ToolResponse Click(JsonElement arguments)
    {
        int x = RequiredCoordinate(arguments, "x");
        int y = RequiredCoordinate(arguments, "y");

        string button = OptionalString(arguments, "button") ?? "left";
        int clicks = OptionalInt(arguments, "clicks", 1);
        string mode = OptionalString(arguments, "channel")
            ?? OptionalString(arguments, "mode")
            ?? "auto";
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);
        bool observe = OptionalBool(arguments, "observe", true);
        bool fallbackInput = OptionalBool(arguments, "fallback_input", false);

        return ToolResponseWriter.FromView(
            _session.Click(x, y, button, clicks, mode, keepForeground, observe, fallbackInput));
    }

    private ToolResponse ClickRef(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "click_ref 需要 ref");

        return ToolResponse.Text(_session.ClickElementRef(
            reference,
            OptionalString(arguments, "channel"),
            OptionalBool(arguments, "keep_foreground", false),
            OptionalBool(arguments, "fallback_input", false)));
    }

    private ToolResponse Gesture(JsonElement arguments) =>
        ToolResponseWriter.FromView(_session.Gesture(arguments));

    private ToolResponse ClickText(JsonElement arguments)
    {
        string text = OptionalString(arguments, "text")
            ?? throw new ToolException("E_INVALID_ARG", "click_text 需要 text");
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);
        bool observe = OptionalBool(arguments, "observe", true);
        bool requireUnique = OptionalBool(arguments, "require_unique", false);
        string channel = OptionalString(arguments, "channel") ?? "auto";
        bool fallbackInput = OptionalBool(arguments, "fallback_input", false);

        int? x = null, y = null, width = null, height = null;

        if (arguments.TryGetProperty("region", out JsonElement region))
        {
            if (region.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            x = OptionalNullableInt(region, "x");
            y = OptionalNullableInt(region, "y");
            width = OptionalNullableInt(region, "width");
            height = OptionalNullableInt(region, "height");

            if (x is null || y is null || width is null || height is null)
            {
                throw new ToolException(
                    "E_INVALID_ARG", "region 需要 x/y/width/height",
                    hint: "省略 region 表示整个虚拟屏", stage: "validate");
            }
        }

        return ToolResponse.Text(_session.ClickText(
            text, keepForeground, observe, channel, requireUnique,
            x, y, width, height, fallbackInput));
    }

    private ToolResponse TypeText(JsonElement arguments)
    {
        string text = OptionalString(arguments, "text")
            ?? throw new ToolException("E_INVALID_ARG", "type_text 需要 text");
        int? pid = OptionalNullableInt(arguments, "pid");
        bool verify = OptionalBool(arguments, "verify", true);
        int chunkChars = OptionalInt(arguments, "chunk_chars", 32);
        string channel = OptionalString(arguments, "channel") ?? "auto";

        return ToolResponseWriter.FromView(_session.TypeText(text, pid, verify, chunkChars, channel));
    }

    private ToolResponse PressKeys(JsonElement arguments)
    {
        bool keepForeground = OptionalBool(arguments, "keep_foreground", false);
        bool allowSystemKeys = OptionalBool(arguments, "allow_system_keys", false);
        bool observe = OptionalBool(arguments, "observe", true);
        int? pid = OptionalNullableInt(arguments, "pid");

        if (arguments.TryGetProperty("sequence", out JsonElement sequence) &&
            sequence.ValueKind == JsonValueKind.Array)
        {
            var keys = new List<string>();

            foreach (JsonElement item in sequence.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ToolException("E_INVALID_ARG", "sequence 必须是字符串数组");
                }

                keys.Add(item.GetString() ?? string.Empty);
            }

            return ToolResponseWriter.FromView(
                _session.PressKeySequence(keys, keepForeground, pid, allowSystemKeys, observe));
        }

        string keysText = OptionalString(arguments, "keys")
            ?? throw new ToolException("E_INVALID_ARG", "press_keys 需要 keys 或 sequence");

        return ToolResponseWriter.FromView(
            _session.PressKeys(keysText, keepForeground, pid, allowSystemKeys, observe));
    }

    private ToolResponse WaitFor(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("condition", out JsonElement condition) ||
            condition.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "wait_for 需要 condition 对象");
        }

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 5000);
        int? intervalMs = OptionalNullableInt(arguments, "interval_ms");
        return ToolResponse.Text(_session.WaitFor(condition, timeoutMs, intervalMs));
    }

    private ToolResponse AssertRegion(JsonElement arguments)
    {
        if (!arguments.TryGetProperty("region", out JsonElement region) ||
            region.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "assert_region 需要 region");
        }

        if (!arguments.TryGetProperty("expectation", out JsonElement expectation) ||
            expectation.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "assert_region 需要 expectation");
        }

        return ToolResponse.Text(_session.AssertRegion(region, expectation));
    }

    private ToolResponse MatchVisual(JsonElement arguments) =>
        ToolResponse.Text(_session.MatchVisual(arguments));

    private ToolResponse ReleaseEnvironment(JsonElement arguments)
    {
        bool keepDisplay = OptionalBool(arguments, "keep_display", true);
        string onDirty = OptionalString(arguments, "on_dirty") ?? "abort";
        bool allowDataLoss = OptionalBool(arguments, "allow_data_loss", false);
        string clientId = _client?.Id ?? "c0";
        ReleaseReport report = _session.ReleaseEnvironment(keepDisplay, onDirty, allowDataLoss);
        JsonObject data = VspJson.ToNode(report).AsObject();
        data["client_id"] = clientId;

        if (data["display_kept"]?.GetValue<bool>() == true)
        {
            data["display_kept_reason"] = _session.Clients.ActiveCount > 1
                ? "other_clients"
                : onDirty == "abort" ? "on_dirty_abort" : "requested";
        }

        // 成功释放后立即把屏幕让给下一个客户端；finally 会再次调用（幂等）。
        // 用 ForceRelease：当前工具自己持有一个租约，IfIdle 会因 active_calls>0 拒绝。
        _session.Lease.ForceRelease(clientId);
        JsonObject lease = _session.Lease.Describe();
        bool leaseHeld = lease["held"]?.GetValue<bool>() == true;
        string? holder = lease["holder_client_id"]?.GetValue<string>();
        int activeCalls = lease["active_calls"]?.GetValue<int>() ?? 0;
        data["screen_lease_released"] =
            !leaseHeld ||
            (string.Equals(holder, clientId, StringComparison.Ordinal) && activeCalls <= 1);
        return ToolResponse.Text(data);
    }

    private ToolResponse GetLog(JsonElement arguments)
    {
        int tail = Math.Clamp(OptionalInt(arguments, "tail", 100), 1, 500);
        string source = (OptionalString(arguments, "source") ?? "memory").Trim().ToLowerInvariant();

        if (source is not ("memory" or "file" or "both"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 source: {source}（可选 memory/file/both）");
        }

        string? level = OptionalString(arguments, "level");

        if (level is not null &&
            level.Trim().ToUpperInvariant() is not ("DEBUG" or "INFO" or "WARN" or "ERROR"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 level: {level}（可选 DEBUG/INFO/WARN/ERROR）",
                new JsonObject { ["field"] = "level" },
                retryable: false,
                hint: "level 只接受 DEBUG/INFO/WARN/ERROR",
                stage: "validate");
        }

        string? contains = OptionalString(arguments, "contains");
        bool includeOther = OptionalBool(arguments, "include_other_clients", false);
        string currentClient = _client?.Id ?? "c0";

        bool Visible(LogEntry entry) =>
            includeOther ||
            entry.ClientId is null ||
            string.Equals(entry.ClientId, currentClient, StringComparison.Ordinal);

        bool Matches(LogEntry entry) =>
            Visible(entry) &&
            (string.IsNullOrWhiteSpace(level) ||
             entry.Level.Equals(level.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(contains) ||
             entry.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var memoryEntries = new List<LogEntryView>();

        foreach (LogEntry entry in _log.Tail(Math.Min(500, Math.Max(tail, tail * 4))))
        {
            if (Matches(entry))
            {
                memoryEntries.Add(ToLogView(entry));
            }
        }

        while (memoryEntries.Count > tail)
        {
            memoryEntries.RemoveAt(0);
        }

        IReadOnlyList<string> fileLines = source is "file" or "both"
            ? _log.ReadFileTail(tail)
            : Array.Empty<string>();

        var fileEntries = new List<LogEntryView>();
        LogEntry? lastFileError = null;

        foreach (string line in fileLines)
        {
            LogEntry? parsed = LogRing.Parse(line);

            if (parsed is null)
            {
                if (string.IsNullOrWhiteSpace(level) && string.IsNullOrWhiteSpace(contains))
                {
                    fileEntries.Add(new LogEntryView(null, "RAW", line, null));
                }
                continue;
            }

            if (!Matches(parsed))
            {
                continue;
            }

            fileEntries.Add(ToLogView(parsed));

            if (string.Equals(parsed.Level, "ERROR", StringComparison.Ordinal))
            {
                lastFileError = parsed;
            }
        }

        LogEntry? lastError = _log.Tail(500)
            .Where(Visible)
            .LastOrDefault(entry => string.Equals(entry.Level, "ERROR", StringComparison.Ordinal))
            ?? lastFileError;

        var view = new LogView(
            source,
            includeOther,
            source == "file" ? fileEntries : memoryEntries,
            _log.FilePath,
            lastError is null ? null : ToLogView(lastError),
            source is "file" or "both" ? fileEntries : null);

        return ToolResponseWriter.FromView(view);
    }


    /// <summary>日志条目 → 视图。time 统一用往返格式（O），与旧输出一致。</summary>
    private static LogEntryView ToLogView(LogEntry entry) =>
        new(
            entry.Time.ToString("O"),
            entry.Level,
            entry.Message,
            string.IsNullOrWhiteSpace(entry.ClientId) ? null : entry.ClientId);

    private ToolResponse RunScript(JsonElement arguments)
    {
        string code = OptionalString(arguments, "code")
            ?? throw new ToolException("E_INVALID_ARG", "run_script 需要 code");

        int timeoutMs = OptionalInt(arguments, "timeout_ms", 30000);
        return ToolResponseWriter.FromView(_session.RunScript(code, timeoutMs));
    }

}
