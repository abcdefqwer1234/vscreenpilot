using System.Text;
using System.Text.Json.Nodes;

namespace VScreenPilot.Session;

/// <summary>
/// 行式紧凑快照格式。比嵌套 JSON 少 3–5 倍 token，并且保留 ref/状态/值。
/// 例：- Button "保存" [e12] {disabled}
/// </summary>
internal static class SnapshotTextFormatter
{
    public static string Render(JsonObject root, bool includeValues, int maxChars = 24000)
    {
        var builder = new StringBuilder(Math.Min(maxChars, 8192));
        Walk(root, 0, includeValues, builder, maxChars);
        return builder.ToString();
    }

    private static bool Walk(
        JsonObject node, int depth, bool includeValues, StringBuilder builder, int maxChars)
    {
        if (builder.Length >= maxChars)
        {
            return false;
        }

        AppendHeader(node, depth, includeValues, builder);
        AppendFlags(node, builder);
        builder.AppendLine();

        if (node["children"] is not JsonArray children)
        {
            return builder.Length < maxChars;
        }

        foreach (JsonNode? child in children)
        {
            if (child is not JsonObject childObject)
            {
                continue;
            }

            if (!Walk(childObject, depth + 1, includeValues, builder, maxChars))
            {
                return false;
            }
        }

        return builder.Length < maxChars;
    }

    private static void AppendHeader(
        JsonObject node, int depth, bool includeValues, StringBuilder builder)
    {
        string controlType = node["control_type"]?.GetValue<string>() ?? "Node";
        string reference = node["ref"]?.GetValue<string>() ?? string.Empty;
        string? name = node["name"]?.GetValue<string>();

        builder.Append(' ', depth * 2);
        builder.Append("- ");
        builder.Append(controlType);

        if (!string.IsNullOrWhiteSpace(name))
        {
            builder.Append(" \"");
            AppendEscaped(builder, name, 80);
            builder.Append('"');
        }

        if (includeValues && node["value"] is JsonValue valueNode &&
            valueNode.TryGetValue(out string? value) && !string.IsNullOrEmpty(value))
        {
            builder.Append(" = \"");
            AppendEscaped(builder, value, 80);
            builder.Append('"');
        }

        if (!string.IsNullOrEmpty(reference))
        {
            builder.Append(" [");
            builder.Append(reference);
            builder.Append(']');
        }
    }

    private static void AppendFlags(JsonObject node, StringBuilder builder)
    {
        var flags = new List<string>(3);

        if (node["enabled"] is JsonValue enabled && enabled.TryGetValue(out bool isEnabled) && !isEnabled)
        {
            flags.Add("disabled");
        }

        if (node["offscreen"] is JsonValue offscreen && offscreen.TryGetValue(out bool isOffscreen) && isOffscreen)
        {
            flags.Add("offscreen");
        }

        if (node["focused"] is JsonValue focused && focused.TryGetValue(out bool isFocused) && isFocused)
        {
            flags.Add("focused");
        }

        if (flags.Count == 0)
        {
            return;
        }

        builder.Append(" {");
        builder.Append(string.Join(',', flags));
        builder.Append('}');
    }

    private static void AppendEscaped(StringBuilder builder, string text, int limit)
    {
        int length = Math.Min(text.Length, limit);

        for (int index = 0; index < length; index++)
        {
            char ch = text[index];

            switch (ch)
            {
                case '\r':
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                default:
                    builder.Append(ch);
                    break;
            }
        }

        if (text.Length > limit)
        {
            builder.Append('…');
        }
    }
}
