using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VScreenPilot.Json;

/// <summary>
/// 唯一的序列化口径：不缩进、不转义中文。所有视图模型 → JSON 都必须走这里，
/// 避免各工具各写一份 JsonSerializerOptions 造成转义/缩进不一致。
/// </summary>
public static class VspJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static JsonNode ToNode<T>(T value) =>
        JsonSerializer.SerializeToNode(value, Options) ?? new JsonObject();

    public static string ToText<T>(T value) => ToNode(value).ToJsonString(Options);
}
