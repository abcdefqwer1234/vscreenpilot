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

        if (token is null)
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

                if (code is null)
                {
                    try
                    {
                        JsonNode? parsed = JsonNode.Parse(text);
                        code = parsed?["code"]?.GetValue<string>();
                        stage = parsed?["stage"]?.GetValue<string>();
                    }
                    catch (Exception exception)
                    {
                        // 非 JSON 文本。
                        LogSink.Debug($"忽略异常: {exception.Message}");
                    }
                }
            }
            else if (type == "image")
            {
                string data = block["data"]?.GetValue<string>() ?? string.Empty;
                imageBytes += data.Length * 3 / 4;
            }
        }

        _metrics.RecordTool(
            tool, milliseconds, code, response.IsError, textBytes, imageBytes);

        _trace?.ToolCall(
            new TraceRequest(tool, requestId, arguments, milliseconds, _clientId),
            new TraceResult(code, response.IsError, textBytes, imageBytes, Channel: null, stage));
    }
}
