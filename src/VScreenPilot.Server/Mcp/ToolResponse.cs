using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VScreenPilot.Mcp;

/// <summary>结构化工具错误。所有对外失败都应转换为该异常。</summary>
public sealed class ToolException : Exception
{
    public string Code { get; }

    public JsonNode? Details { get; }

    public bool? Retryable { get; }

    public string? Hint { get; }

    public string? Stage { get; }

    public ToolException(
        string code,
        string message,
        JsonNode? details = null,
        bool? retryable = null,
        string? hint = null,
        string? stage = null) : base(message)
    {
        Code = code;
        Details = details;
        Retryable = retryable;
        Hint = hint;
        Stage = stage;
    }
}

public sealed class ToolResponse
{
    // 不把中文转成 \uXXXX：可读性更好，也直接减少返回字节数。
    private static readonly JsonSerializerOptions Compact = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public JsonArray Content { get; } = new();

    /// <summary>与 text 内容等价的机器可读结果，供支持 structuredContent 的客户端使用。</summary>
    public JsonNode? StructuredContent { get; private set; }

    public bool IsError { get; set; }

    public static ToolResponse Text(JsonNode payload)
    {
        // text 立即序列化；structuredContent 用独立副本，避免“Text() 之后再改 payload”
        // 造成 text 与 structuredContent 不一致（snapshot 大图路径曾踩过这个坑）。
        var response = new ToolResponse
        {
            StructuredContent = payload.DeepClone(),
        };
        response.Content.Add((JsonNode)new JsonObject
        {
            ["type"] = "text",
            ["text"] = payload.ToJsonString(Compact),
        });
        return response;
    }

    public static ToolResponse Error(
        string code,
        string message,
        JsonNode? details = null,
        bool? retryable = null,
        string? hint = null,
        string? stage = null)
    {
        // 统一从错误目录补齐 retryable/hint/stage，避免各工具各写一份、缺字段。
        ErrorInfo info = ErrorCatalog.Get(code);

        var payload = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };

        if (details is not null)
        {
            payload["details"] = details;
        }

        payload["retryable"] = retryable ?? info.Retryable;

        string effectiveHint = string.IsNullOrWhiteSpace(hint) ? info.Hint : hint;
        if (!string.IsNullOrWhiteSpace(effectiveHint))
        {
            payload["hint"] = effectiveHint;
        }

        payload["stage"] = string.IsNullOrWhiteSpace(stage) ? info.Stage : stage;
        payload["docs"] = $"docs/errors.md#{code.ToLowerInvariant()}";

        var response = Text(payload);
        response.IsError = true;
        return response;
    }

    public static ToolResponse FromException(ToolException exception) =>
        Error(
            exception.Code,
            exception.Message,
            exception.Details,
            exception.Retryable,
            exception.Hint,
            exception.Stage);

    /// <summary>未包装异常的最后一道兜底：映射成稳定错误码，而不是原样抛 E_INTERNAL。</summary>
    public static ToolResponse FromException(Exception exception, string stage = "internal") =>
        FromException(ErrorCatalog.Normalize(exception, stage));

    /// <summary>把 request_id 写入 structuredContent 与 text，便于客户端和服务端日志对账。</summary>
    public ToolResponse SetRequestId(string requestId)
    {
        if (StructuredContent is JsonObject structured)
        {
            structured["request_id"] = requestId;
        }

        foreach (JsonNode? block in Content)
        {
            if (block is JsonObject textBlock &&
                string.Equals(textBlock["type"]?.GetValue<string>(), "text", StringComparison.Ordinal) &&
                StructuredContent is not null)
            {
                textBlock["text"] = StructuredContent.ToJsonString(Compact);
            }
        }

        return this;
    }

    public ToolResponse AddImage(byte[] png, string mimeType = "image/png")
    {
        Content.Add((JsonNode)new JsonObject
        {
            ["type"] = "image",
            ["data"] = Convert.ToBase64String(png),
            ["mimeType"] = mimeType,
        });
        return this;
    }

    public ToolResponse AddText(JsonNode payload)
    {
        Content.Add((JsonNode)new JsonObject
        {
            ["type"] = "text",
            ["text"] = payload.ToJsonString(Compact),
        });
        return this;
    }
}
