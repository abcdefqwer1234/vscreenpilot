using System.Text.Json;
using System.Text.Json.Nodes;

namespace VScreenPilot.Mcp;

/// <summary>
/// 输入参数校验：覆盖工具 schema 实际用到的 JSON Schema 子集
/// （type/properties/required/additionalProperties/enum/const/min/max/items/oneOf）。
/// 不做 $ref 等完整 draft 语义；嵌套字段也会递归检查，避免拼错时静默用默认值。
/// </summary>
internal static class ArgumentValidator
{
    public static void Validate(JsonNode? schema, JsonElement value, string path = "")
    {
        if (schema is not JsonObject rules)
        {
            return;
        }

        ValidateType(rules["type"], value, path);

        if (rules["enum"] is JsonArray allowed && !allowed.Any(item => Matches(item, value)))
        {
            throw Invalid(path, "enum", value, string.Join("|", allowed.Select(ToText)));
        }

        if (rules["const"] is JsonNode constant && !Matches(constant, value))
        {
            throw Invalid(path, "const", value, constant.ToJsonString());
        }

        if (value.ValueKind == JsonValueKind.Number)
        {
            double number = value.GetDouble();

            if (rules["minimum"] is JsonValue minimum && number < minimum.GetValue<double>())
            {
                throw Invalid(path, ">=", value, minimum.ToJsonString());
            }

            if (rules["maximum"] is JsonValue maximum && number > maximum.GetValue<double>())
            {
                throw Invalid(path, "<=", value, maximum.ToJsonString());
            }
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            string text = value.GetString() ?? string.Empty;

            if (rules["minLength"] is JsonValue minLength && text.Length < minLength.GetValue<int>())
            {
                throw Invalid(path, $"length>={minLength}", value, null);
            }

            if (rules["maxLength"] is JsonValue maxLength && text.Length > maxLength.GetValue<int>())
            {
                throw Invalid(path, $"length<={maxLength}", value, null);
            }
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            ValidateObject(rules, value, path);
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            ValidateArray(rules, value, path);
        }

        if (rules["oneOf"] is JsonArray oneOf)
        {
            ValidateOneOf(oneOf, value, path);
        }

        if (rules["anyOf"] is JsonArray anyOf && !anyOf.Any(item => IsValid(item, value)))
        {
            throw Invalid(path, "anyOf", value, null);
        }
    }

    private static void ValidateObject(JsonObject rules, JsonElement value, string path)
    {
        var properties = rules["properties"] as JsonObject;

        if (rules["required"] is JsonArray required)
        {
            foreach (JsonNode? item in required)
            {
                string name = item?.GetValue<string>() ?? string.Empty;

                if (!value.TryGetProperty(name, out _))
                {
                    throw new ToolException(
                        "E_INVALID_ARG",
                        $"缺少必填参数 {Join(path, name)}",
                        new JsonObject { ["path"] = Join(path, name), ["expected"] = "required" },
                        retryable: false,
                        hint: "补齐必填字段后重试",
                        stage: "validate");
                }
            }
        }

        foreach (JsonProperty property in value.EnumerateObject())
        {
            string childPath = Join(path, property.Name);

            if (properties is not null && properties.TryGetPropertyValue(property.Name, out JsonNode? childSchema))
            {
                Validate(childSchema, property.Value, childPath);
                continue;
            }

            bool allowed = rules["additionalProperties"] switch
            {
                JsonValue boolean => boolean.GetValue<bool>(),
                JsonObject extra => IsValid(extra, property.Value),
                _ => true,
            };

            if (!allowed)
            {
                var known = new JsonArray();

                if (properties is not null)
                {
                    foreach ((string key, JsonNode? _) in properties)
                    {
                        known.Add(key);
                    }
                }

                throw new ToolException(
                    "E_INVALID_ARG",
                    $"未知参数 {childPath}",
                    new JsonObject { ["path"] = childPath, ["allowed"] = known },
                    retryable: false,
                    hint: "检查字段名拼写；allowed 列出了该层接受的字段",
                    stage: "validate");
            }

            if (rules["additionalProperties"] is JsonObject extraSchema)
            {
                Validate(extraSchema, property.Value, childPath);
            }
        }
    }

    private static void ValidateArray(JsonObject rules, JsonElement value, string path)
    {
        int count = value.GetArrayLength();

        if (rules["minItems"] is JsonValue minItems && count < minItems.GetValue<int>())
        {
            throw Invalid(path, $"items>={minItems}", value, null);
        }

        if (rules["maxItems"] is JsonValue maxItems && count > maxItems.GetValue<int>())
        {
            throw Invalid(path, $"items<={maxItems}", value, null);
        }

        if (rules["items"] is JsonNode itemSchema)
        {
            int index = 0;

            foreach (JsonElement item in value.EnumerateArray())
            {
                Validate(itemSchema, item, $"{path}[{index++}]");
            }
        }
    }

    private static void ValidateOneOf(JsonArray oneOf, JsonElement value, string path)
    {
        int matches = 0;

        foreach (JsonNode? branch in oneOf)
        {
            if (IsValid(branch, value))
            {
                matches++;
            }
        }

        if (matches == 1)
        {
            return;
        }

        throw new ToolException(
            "E_INVALID_ARG",
            matches == 0
                ? $"参数 {path} 不匹配任何允许的结构"
                : $"参数 {path} 同时匹配多个互斥结构（schema 需要收紧）",
            new JsonObject { ["path"] = path, ["matches"] = matches },
            retryable: false,
            hint: "按工具描述选择一种结构，并检查嵌套字段名",
            stage: "validate");
    }

    private static bool IsValid(JsonNode? schema, JsonElement value)
    {
        try
        {
            Validate(schema, value);
            return true;
        }
        catch (ToolException)
        {
            return false;
        }
    }

    private static void ValidateType(JsonNode? typeNode, JsonElement value, string path)
    {
        if (typeNode is null)
        {
            return;
        }

        var allowed = new List<string>();

        if (typeNode is JsonArray array)
        {
            allowed.AddRange(array.Select(item => item?.GetValue<string>() ?? string.Empty));
        }
        else if (typeNode is JsonValue scalar)
        {
            allowed.Add(scalar.GetValue<string>());
        }
        else
        {
            return;
        }

        if (allowed.Any(type => MatchesType(type, value)))
        {
            return;
        }

        throw Invalid(path, string.Join("|", allowed), value, null);
    }

    private static bool MatchesType(string type, JsonElement value) => type switch
    {
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "null" => value.ValueKind == JsonValueKind.Null,
        _ => true,
    };

    private static bool Matches(JsonNode? expected, JsonElement actual)
    {
        if (expected is null)
        {
            return actual.ValueKind == JsonValueKind.Null;
        }

        using JsonDocument document = JsonDocument.Parse(expected.ToJsonString());
        return JsonElement.DeepEquals(document.RootElement, actual);
    }

    private static string ToText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue(out string? text)
            ? text
            : node?.ToJsonString() ?? "null";

    private static ToolException Invalid(
        string path, string expected, JsonElement actual, string? expectedValue)
    {
        var details = new JsonObject
        {
            ["path"] = path.Length == 0 ? "$" : path,
            ["expected"] = expected,
            ["actual"] = actual.ValueKind.ToString(),
        };

        if (expectedValue is not null)
        {
            details["expected_value"] = expectedValue;
        }

        if (actual.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True
            or JsonValueKind.False)
        {
            details["actual_value"] = actual.GetRawText();
        }

        return new ToolException(
            "E_INVALID_ARG",
            $"参数 {details["path"]} 需要 {expected}（收到 {actual.ValueKind}）",
            details,
            retryable: false,
            hint: "修正类型/取值后重试；嵌套字段不会被静默忽略",
            stage: "validate");
    }

    private static string Join(string path, string name) =>
        path.Length == 0 ? name : $"{path}.{name}";
}
