using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Perception;
using VScreenPilot.Session;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

internal sealed partial class McpTransport
{
    private bool TryAcquireRateLimit(out int retryAfterMs)
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

    /// <summary>参数摘要用于审计：文本/脚本等敏感字段只记录长度。</summary>
    private static string? SummarizeArguments(JsonElement arguments)
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

    private void ReportProgress(
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

    private void Trace(
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

        _session.Metrics.RecordTool(
            tool, milliseconds, code, response.IsError, textBytes, imageBytes);

        _trace?.ToolCall(tool, requestId, arguments, milliseconds, code, response.IsError,
            textBytes, imageBytes, channel: null, stage: stage, clientId: _client?.Id ?? "c0");
    }

}
