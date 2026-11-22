using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>
/// 隐私脱敏：邮箱替换为 <c>***@domain</c>，并递归处理 JSON 树里的所有字符串。
/// 不依赖会话状态，可独立单测。
/// </summary>
internal static class PrivacyRedactor
{
    private static readonly Regex EmailPattern = new(
        @"[A-Za-z0-9._%+\-]+@([A-Za-z0-9.\-]+\.[A-Za-z]{2,})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string RedactEmails(string input, ref int count)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('@'))
        {
            return input;
        }

        int matches = 0;
        string result = EmailPattern.Replace(input, match =>
        {
            matches++;
            return $"***@{match.Groups[1].Value}";
        });

        count += matches;
        return result;
    }

    /// <summary>递归脱敏 Json 树里的所有字符串（tree/windows/foreground/annotations/text）。</summary>
    public static void RedactJsonStrings(JsonNode? node, ref int count)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (string key in obj.Select(pair => pair.Key).ToList())
                {
                    RedactObjectValue(obj, key, ref count);
                }

                break;

            case JsonArray array:
                for (int index = 0; index < array.Count; index++)
                {
                    RedactArrayValue(array, index, ref count);
                }

                break;
        }
    }

    private static void RedactObjectValue(JsonObject obj, string key, ref int count)
    {
        JsonNode? value = obj[key];

        if (TryRedactScalar(value, ref count, out string redacted))
        {
            obj[key] = redacted;
            return;
        }

        if (value is not JsonValue)
        {
            RedactJsonStrings(value, ref count);
        }
    }

    private static void RedactArrayValue(JsonArray array, int index, ref int count)
    {
        JsonNode? value = array[index];

        if (TryRedactScalar(value, ref count, out string redacted))
        {
            array[index] = redacted;
            return;
        }

        if (value is not JsonValue)
        {
            RedactJsonStrings(value, ref count);
        }
    }

    private static bool TryRedactScalar(JsonNode? value, ref int count, out string redacted)
    {
        redacted = string.Empty;

        if (value is not JsonValue scalar ||
            !scalar.TryGetValue(out string? text) ||
            text is null)
        {
            return false;
        }

        string result = RedactEmails(text, ref count);

        if (string.Equals(result, text, StringComparison.Ordinal))
        {
            return false;
        }

        redacted = result;
        return true;
    }
}
