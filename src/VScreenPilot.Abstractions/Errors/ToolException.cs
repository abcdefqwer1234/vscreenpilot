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
