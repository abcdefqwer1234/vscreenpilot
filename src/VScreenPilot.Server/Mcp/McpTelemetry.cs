using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;
using VScreenPilot.Platform.Logging;
using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

/// <summary>一次 MCP 传输所需的全量依赖；用 init 属性避免长参数构造。</summary>
internal sealed record McpTransportOptions
{
    public ToolServices Services { get; init; } = null!;
    public IMcpRuntime Runtime { get; init; } = null!;
    public IExecutionControl Control { get; init; } = null!;
    public WriteScheduler Scheduler { get; init; } = null!;
    public LogRing Log { get; init; } = null!;
    public ClientInfo? Client { get; init; }
    public TraceLog? Trace { get; init; }
    public bool TraceEnabled { get; init; } = true;
    public bool ReadOnly { get; init; }
}

/// <summary>请求限速、进度通知、参数摘要与 trace/metrics 记录。</summary>
internal sealed class McpTelemetry
{
    private const double RateCapacity = 300;
    private const double RateRefillPerSecond = 100;

    private readonly MetricsRegistry _metrics;
    private readonly TraceLog? _trace;
    private readonly string _clientId;
    private readonly object _rateGate = new();
    private double _rateTokens = RateCapacity;
    private long _rateLastTicks = Environment.TickCount64;

    public McpTelemetry(MetricsRegistry metrics, TraceLog? trace, string clientId)
    {
        _metrics = metrics;
        _trace = trace;
        _clientId = clientId;
    }

    public bool TryAcquireRateLimit(out int retryAfterMs)
    {
        retryAfterMs = 0;

        lock (_rateGate)
        {
            long now = Environment.TickCount64;
            double elapsed = Math.Max(0, now - _rateLastTicks) / 1000.0;
            _rateLastTicks = now;
            _rateTokens = Math.Min(RateCapacity, _rateTokens + elapsed * RateRefillPerSecond);

            if (_rateTokens >= 1)
            {
                _rateTokens -= 1;
                return true;
            }

            retryAfterMs = (int)Math.Ceiling((1 - _rateTokens) / RateRefillPerSecond * 1000);
            return false;
        }
    }

    /// <summary>参数摘要：截断长文本；改写/脚本/路径类字段只记录长度。</summary>
    public static string? SummarizeArguments(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var copy = new JsonObject();

        foreach (JsonProperty property in arguments.EnumerateObject())
        {
            if (property.Name is "text" or "value" or "code" or "args" or "password"
                or "window" or "path" or "save_path" or "target_path")
            {
                string raw = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.GetRawText();
                copy[property.Name] = $"<redacted:{raw.Length}>";
                continue;
            }

            copy[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        string json = copy.ToJsonString();
        return json.Length <= 300 ? json : json[..300] + "…";
    }

    public void ReportProgress(
        RequestContext<CallToolRequestParams> context, int current, int total, string message)
    {
        JsonNode? token = context.Params?.Meta?["progressToken"];

        // 双保险：即使调用方绕过 ToolCallPipeline，也不会在默认配置下发出通知。
        if (token is null || !ProgressPolicy.Enabled)
        {
            return;
        }

        try
        {
            _ = context.Server.SendNotificationAsync(
                "notifications/progress",
                new JsonObject
                {
                    ["progressToken"] = token.DeepClone(),
                    ["progress"] = current,
                    ["total"] = total,
                    ["message"] = message,
                });
        }
        catch (Exception exception)
        {
            // 进度是尽力而为。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }

    public void Trace(
        string tool, string requestId, string? arguments, double milliseconds, ToolResponse response)
    {
        string? code = null;
        string? stage = null;
        string? summary = null;
        int textBytes = 0;
        int imageBytes = 0;

        foreach (JsonNode? item in response.Content)
        {
            if (item is not JsonObject block)
            {
                continue;
            }

            string? type = block["type"]?.GetValue<string>();

            if (type == "text")
            {
                string text = block["text"]?.GetValue<string>() ?? string.Empty;
                textBytes += System.Text.Encoding.UTF8.GetByteCount(text);
            }
            else if (type == "image")
            {
                string data = block["data"]?.GetValue<string>() ?? string.Empty;
                imageBytes += data.Length * 3 / 4;
            }
        }

        // structuredContent 已经是解析好的 payload：直接读 code/stage，
        // 不再对整段 snapshot 文本做 JsonNode.Parse（大快照上这是明显的额外开销）。
        if (response.StructuredContent is JsonObject structured)
        {
            code = ReadString(structured["code"]);
            stage = ReadString(structured["stage"]);
            summary = BuildSummary(structured);
        }

        _metrics.RecordTool(
            tool, milliseconds, code, response.IsError, textBytes, imageBytes);

        _trace?.ToolCall(
            new TraceRequest(tool, requestId, arguments, milliseconds, _clientId),
            new TraceResult(code, response.IsError, textBytes, imageBytes, Channel: null, stage, summary));
    }

    /// <summary>
    /// 只记录可复核的状态字段，不落全文：用于把“哪一步改了什么、是否被确认”
    /// 与 request_id 关联起来，同时避免 trace 体积失控。
    /// </summary>
    internal static string? BuildSummary(JsonObject structured)
    {
        var summary = new JsonObject();
        string[] fields =
        [
            "ref", "snapshot_id", "frame_id", "target_pid", "hwnd",
            "verified", "truncated", "method", "channel", "mode",
        ];

        foreach (string field in fields)
        {
            if (structured[field] is { } value)
            {
                summary[field] = value.DeepClone();
            }
        }

        if (structured["observed"] is JsonObject observed &&
            observed["changed"] is { } changed)
        {
            summary["changed"] = changed.DeepClone();
        }

        return summary.Count == 0 ? null : summary.ToJsonString();
    }

    private static string? ReadString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
}
