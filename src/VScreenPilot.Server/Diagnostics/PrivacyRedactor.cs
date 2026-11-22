using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VScreenPilot.Diagnostics;

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
                {
                    foreach (string key in obj.Select(pair => pair.Key).ToList())
                    {
                        JsonNode? value = obj[key];

                        if (value is JsonValue scalar && scalar.TryGetValue(out string? text) &&
                            text is not null)
                        {
                            string redacted = RedactEmails(text, ref count);

                            if (!ReferenceEquals(redacted, text) &&
                                !string.Equals(redacted, text, StringComparison.Ordinal))
                            {
                                obj[key] = redacted;
                            }
                        }
                        else
                        {
                            RedactJsonStrings(value, ref count);
                        }
                    }

                    break;
                }

            case JsonArray array:
                {
                    for (int index = 0; index < array.Count; index++)
                    {
                        JsonNode? value = array[index];

                        if (value is JsonValue scalar && scalar.TryGetValue(out string? text) &&
                            text is not null)
                        {
                            string redacted = RedactEmails(text, ref count);

                            if (!string.Equals(redacted, text, StringComparison.Ordinal))
                            {
                                array[index] = redacted;
                            }
                        }
                        else
                        {
                            RedactJsonStrings(value, ref count);
                        }
                    }

                    break;
                }
        }
    }
}
