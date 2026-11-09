using System.Text.Json.Nodes;

namespace VScreenPilot.Mcp;

/// <summary>结构化工具错误。所有对外失败都应转换为该异常。</summary>
public sealed class ToolException : Exception
{
    public string Code { get; }

    public JsonNode? Details { get; }

    public ToolException(string code, string message, JsonNode? details = null) : base(message)
    {
        Code = code;
        Details = details;
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

    public static ToolResponse Error(string code, string message, JsonNode? details = null)
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

        var response = Text(payload);
        response.IsError = true;
        return response;
    }

    public static ToolResponse FromException(ToolException exception) =>
        Error(exception.Code, exception.Message, exception.Details);

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
