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
    public JsonArray Content { get; } = new();

    public bool IsError { get; set; }

    public static ToolResponse Text(JsonNode payload)
    {
        var response = new ToolResponse();
        response.Content.Add((JsonNode)new JsonObject
        {
            ["type"] = "text",
            ["text"] = payload.ToJsonString(),
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
        var payload = new JsonObject
        {
            ["code"] = code,
            ["message"] = message,
        };

        if (details is not null)
        {
            payload["details"] = details;
        }

        if (retryable is not null)
        {
            payload["retryable"] = retryable.Value;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            payload["hint"] = hint;
        }

        if (!string.IsNullOrWhiteSpace(stage))
        {
            payload["stage"] = stage;
        }

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
            ["text"] = payload.ToJsonString(),
        });
        return this;
    }
}
