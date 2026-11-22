using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using VScreenPilot.Tools;

namespace VScreenPilot.Mcp;

/// <summary>ToolResponse → MCP CallToolResult 的唯一映射点。</summary>
internal static class ToolResultMapper
{
    public static CallToolResult ToResult(ToolResponse response)
    {
        var content = new List<ContentBlock>();

        foreach (JsonNode? item in response.Content)
        {
            if (item is not JsonObject block)
            {
                continue;
            }

            string? type = block["type"]?.GetValue<string>();

            if (type == "text")
            {
                content.Add(new TextContentBlock
                {
                    Text = block["text"]?.GetValue<string>() ?? string.Empty,
                });
            }
            else if (type == "image")
            {
                string base64 = block["data"]?.GetValue<string>() ?? string.Empty;

                // ImageContentBlock.Data 的契约是“base64 编码后的 UTF-8 字节”，
                // 不是原始 PNG 字节。用 FromBytes 让 SDK 负责编码；直接把
                // Convert.FromBase64String 的结果塞进 Data 会让客户端解码失败（N1）。
                content.Add(ImageContentBlock.FromBytes(
                    Convert.FromBase64String(base64),
                    block["mimeType"]?.GetValue<string>() ?? "image/png"));
            }
            else if (type == "resource_link")
            {
                content.Add(new ResourceLinkBlock
                {
                    Uri = block["uri"]?.GetValue<string>() ?? string.Empty,
                    Name = block["name"]?.GetValue<string>() ?? "artifact",
                    MimeType = block["mimeType"]?.GetValue<string>(),
                    Size = block["size"]?.GetValue<long>(),
                });
            }
        }

        return new CallToolResult
        {
            Content = content,
            StructuredContent = response.StructuredContent is null
                ? null
                : JsonDocument.Parse(response.StructuredContent.ToJsonString()).RootElement.Clone(),
            IsError = response.IsError,
        };
    }
}
