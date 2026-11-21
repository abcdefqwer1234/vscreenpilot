using System.Text.Json.Nodes;

namespace VScreenPilot.Errors;

/// <summary>
/// 常见错误 details 的统一构造。放在 Abstractions，供 Automation/Platform 复用，
/// 避免领域层各自拼 JsonObject。
/// </summary>
public static class ToolDetails
{
    public static JsonObject ScriptToken(string token) =>
        new() { ["token"] = token };

    public static JsonObject InvalidControlType(string field, IEnumerable<string> supported)
    {
        var array = new JsonArray();

        foreach (string item in supported)
        {
            array.Add(item);
        }

        return new JsonObject
        {
            ["field"] = field,
            ["supported"] = array,
        };
    }
}
