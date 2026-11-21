using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Errors;

namespace VScreenPilot.Json;

/// <summary>
/// JSON 参数读取的唯一实现。三种口径必须保持区分，不能合并成一种：
/// <list type="bullet">
/// <item>严格（<c>Optional*/Required*</c>）：类型不符抛 <see cref="ToolException"/>；MCP 工具参数用。</item>
/// <item>宽松（<c>Get*</c>）：类型不符回退默认值；会话内部与视觉条件用。</item>
/// <item>ctl（<c>GetX(JsonObject)</c>）：控制管道请求用，同样是宽松语义。</item>
/// </list>
/// 注意 <c>JsonElement.TryGetInt32</c> 在非 Number 上可能抛异常，所以先判 <c>ValueKind</c>。
/// </summary>
internal static class JsonArgReader
{
    // ---------------- 严格（MCP 工具参数） ----------------

    public static string? OptionalString(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        throw InvalidType(property, "string", value.ValueKind);
    }

    public static int RequiredCoordinate(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            throw new ToolException("E_INVALID_ARG", $"click 需要 {property}");
        }

        if (!value.TryGetInt32(out int result))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"click 的 {property} 必须是整数（收到 {value.ValueKind}）");
        }

        return result;
    }

    public static int OptionalInt(JsonElement element, string property, int fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result))
        {
            return result;
        }

        throw InvalidType(property, "integer", value.ValueKind);
    }

    public static int RequiredInt(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(property, out JsonElement value) &&
        value.TryGetInt32(out int result)
            ? result
            : throw new ToolException("E_INVALID_ARG", $"缺少整数参数 {property}");

    public static double OptionalDouble(JsonElement element, string property, double fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double result))
        {
            return result;
        }

        throw InvalidType(property, "number", value.ValueKind);
    }

    public static bool OptionalBool(JsonElement element, string property, bool fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidType(property, "boolean", value.ValueKind),
        };
    }

    public static int? OptionalNullableInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int result))
        {
            return result;
        }

        throw InvalidType(property, "integer", value.ValueKind);
    }

    public static bool? OptionalNullableBool(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw InvalidType(property, "boolean", value.ValueKind),
        };
    }

    private static ToolException InvalidType(string property, string expected, JsonValueKind actual) =>
        new(
            "E_INVALID_ARG",
            $"参数 {property} 必须是 {expected}（收到 {actual}）",
            new JsonObject { ["field"] = property, ["expected"] = expected, ["actual"] = actual.ToString() },
            retryable: false,
            hint: "修正参数类型后重试；不会使用默认值静默吞掉",
            stage: "validate");

    // ---------------- 宽松（内部/视觉条件） ----------------

    public static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static int GetInt(JsonElement element, string property, int fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        return value.TryGetInt32(out int result) ? result : fallback;
    }

    public static double GetDouble(JsonElement element, string property, double fallback)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Number)
        {
            return fallback;
        }

        return value.TryGetDouble(out double result) ? result : fallback;
    }

    public static bool GetBool(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out JsonElement value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;

    // ---------------- ctl（控制管道） ----------------

    public static int GetInt(JsonObject request, string name, int fallback) =>
        request[name] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : fallback;

    public static int? GetNullableInt(JsonObject request, string name) =>
        request[name] is JsonValue value && value.TryGetValue(out int parsed) ? parsed : null;

    public static bool GetBool(JsonObject request, string name, bool fallback) =>
        request[name] is JsonValue value && value.TryGetValue(out bool parsed) ? parsed : fallback;
}
