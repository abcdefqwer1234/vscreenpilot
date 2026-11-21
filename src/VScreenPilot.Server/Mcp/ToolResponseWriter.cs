using System.Text.Json.Nodes;
using VScreenPilot.Json;

namespace VScreenPilot.Mcp;

/// <summary>统一的工具响应出口：视图/节点 → text + structuredContent。</summary>
internal static class ToolResponseWriter
{
    public static ToolResponse FromView<T>(T view) => ToolResponse.Text(VspJson.ToNode(view));

    public static ToolResponse FromNode(JsonNode payload) => ToolResponse.Text(payload);
}
