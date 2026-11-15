using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Core.Displays;
using VScreenPilot.Mcp;
using VScreenPilot.Perception;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    /// <summary>
    /// OCR 读取屏幕文本。坐标返回的是虚拟屏绝对坐标，可直接用于 click。
    /// </summary>
    public JsonObject ReadScreenText(
        int? x, int? y, int? width, int? height, string? query, int maxLines)
    {
        DisplayInfo display = EnsureEnvironmentFast();
        var stopwatch = Stopwatch.StartNew();
        int left = display.X;
        int top = display.Y;
        int regionWidth = display.Width;
        int regionHeight = display.Height;

        if (x is not null && y is not null && width is not null && height is not null)
        {
            RequireRegionInDisplay(display, x.Value, y.Value, width.Value, height.Value, "read_screen_text");
            left = x.Value;
            top = y.Value;
            regionWidth = width.Value;
            regionHeight = height.Value;
        }

        (byte[] bgra, int captureWidth, int captureHeight) =
            CaptureRegion(left, top, regionWidth, regionHeight);

        List<ScreenTextLine> lines;
        bool preferLatin = !string.IsNullOrWhiteSpace(query) && IsAscii(query!);

        try
        {
            lines = OcrService.Read(
                bgra, captureWidth, captureHeight,
                out string engine, out string language, preferLatin);
            return BuildTextResult(
                lines, left, top, captureWidth, captureHeight, query, maxLines, engine, language, stopwatch);
        }
        catch (NotSupportedException exception)
        {
            throw new ToolException(
                "E_NOT_SUPPORTED",
                exception.Message,
                retryable: false,
                hint: "安装系统 OCR 语言包后重试；或改用 snapshot/match_visual",
                stage: "ocr");
        }
        catch (Exception exception) when (exception is not ToolException)
        {
            throw new ToolException(
                "E_INTERNAL",
                $"OCR 失败: {exception.Message}",
                retryable: true,
                hint: "缩小 region 或重试；持续失败用 get_log 查看",
                stage: "ocr");
        }
    }

    private static JsonObject BuildTextResult(
        List<ScreenTextLine> lines,
        int originX, int originY, int width, int height,
        string? query, int maxLines,
        string engine, string language,
        Stopwatch stopwatch)
    {
        var all = new JsonArray();
        var matches = new JsonArray();
        int limit = Math.Clamp(maxLines, 1, 200);

        foreach (ScreenTextLine line in lines)
        {
            var item = new JsonObject
            {
                ["text"] = line.Text,
                ["bounds"] = new JsonObject
                {
                    ["x"] = originX + line.Left,
                    ["y"] = originY + line.Top,
                    ["width"] = line.Width,
                    ["height"] = line.Height,
                },
            };

            if (!string.IsNullOrWhiteSpace(query) &&
                OcrTextMatches(line.Text, query!))
            {
                matches.Add(item.DeepClone());
            }

            if (all.Count < limit)
            {
                all.Add(item);
            }
        }

        return new JsonObject
        {
            ["engine"] = engine,
            ["language"] = language,
            ["lines"] = all,
            ["matches"] = matches,
            ["count"] = lines.Count,
            ["region"] = new JsonObject
            {
                ["x"] = originX,
                ["y"] = originY,
                ["width"] = width,
                ["height"] = height,
            },
            ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1),
        };
    }

    /// <summary>OCR 定位 + 点击 + 前后差异，供 click_ref 找不到元素的画布应用使用。</summary>
    public JsonObject ClickText(
        string text, bool keepForeground = false, bool observe = true, string channel = "auto")
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ToolException("E_INVALID_ARG", "click_text 需要非空 text");
        }

        DisplayInfo display = EnsureEnvironmentFast();
        (byte[] bgra, int captureWidth, int captureHeight) =
            CaptureRegion(display.X, display.Y, display.Width, display.Height);

        List<ScreenTextLine> lines;

        try
        {
            lines = OcrService.Read(
                bgra, captureWidth, captureHeight, out _, out _, IsAscii(text));
        }
        catch (NotSupportedException exception)
        {
            throw new ToolException(
                "E_NOT_SUPPORTED",
                exception.Message,
                retryable: false,
                hint: "安装系统 OCR 语言包后重试",
                stage: "ocr");
        }

        ScreenTextLine? match = lines.FirstOrDefault(
            line => OcrTextMatches(line.Text, text));

        if (match is null)
        {
            throw new ToolException(
                "E_NO_MATCH",
                $"OCR 未找到文本: {text}",
                new JsonObject { ["text"] = text, ["lines"] = lines.Count },
                retryable: true,
                hint: "先用 read_screen_text 看 OCR 结果；或缩小查询词、换 region",
                stage: "ocr");
        }

        int clickX = display.X + match.Left + match.Width / 2;
        int clickY = display.Y + match.Top + match.Height / 2;
        JsonObject click = Click(clickX, clickY, "left", 1, channel, keepForeground, observe);

        return new JsonObject
        {
            ["found"] = true,
            ["text"] = match.Text,
            ["x"] = clickX,
            ["y"] = clickY,
            ["bounds"] = new JsonObject
            {
                ["x"] = display.X + match.Left,
                ["y"] = display.Y + match.Top,
                ["width"] = match.Width,
                ["height"] = match.Height,
            },
            ["method"] = click["method"]?.GetValue<string>(),
            ["observed"] = click["observed"]?.DeepClone(),
        };
    }

    private static bool IsAscii(string value)
    {
        foreach (char ch in value)
        {
            if (ch > 127)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 中文 OCR 引擎会把英文短横线识别成“һ/一”等字符，并插入空格。
    /// 精确匹配失败后，用“只保留字母数字”的归一化再比一次。
    /// </summary>
    internal static bool OcrTextMatches(string haystack, string needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
        {
            return false;
        }

        if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string normalizedNeedle = NormalizeOcr(needle);
        string normalizedHaystack = NormalizeOcr(haystack);

        if (normalizedNeedle.Length < 3)
        {
            return false;
        }

        if (normalizedHaystack.Contains(normalizedNeedle, StringComparison.Ordinal))
        {
            return true;
        }

        // 中文 OCR 可能吞掉尾部字符；用前缀兜底，但至少 6 个字母数字。
        string prefix = normalizedNeedle[..Math.Min(6, normalizedNeedle.Length)];
        return normalizedHaystack.Contains(prefix, StringComparison.Ordinal);
    }

    private static string NormalizeOcr(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (char ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
