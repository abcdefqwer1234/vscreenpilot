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

        try
        {
            lines = OcrService.Read(
                bgra, captureWidth, captureHeight,
                out string engine, out string language, query ?? string.Empty,
                Cancellation);
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
        catch (OperationCanceledException)
        {
            throw new ToolException(
                "E_CANCELLED",
                "OCR 已被取消",
                retryable: true,
                hint: "重新调用 read_screen_text；已取消的结果不会返回",
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
        int redactions = 0;

        foreach (ScreenTextLine line in lines)
        {
            string redacted = RedactEmails(line.Text, ref redactions);

            var item = new JsonObject
            {
                ["text"] = redacted,
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
            ["returned"] = all.Count,
            ["truncated"] = lines.Count > all.Count,
            ["privacy_redactions"] = redactions,
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
        string text, bool keepForeground = false, bool observe = true,
        string channel = "auto", bool requireUnique = false,
        int? regionX = null, int? regionY = null,
        int? regionWidth = null, int? regionHeight = null,
        bool fallbackInput = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ToolException("E_INVALID_ARG", "click_text 需要非空 text");
        }

        DisplayInfo display = EnsureEnvironmentFast();
        int left = display.X;
        int top = display.Y;
        int width = display.Width;
        int height = display.Height;

        if (regionX is not null && regionY is not null &&
            regionWidth is not null && regionHeight is not null)
        {
            RequireRegionInDisplay(
                display, regionX.Value, regionY.Value, regionWidth.Value, regionHeight.Value,
                "click_text");
            left = regionX.Value;
            top = regionY.Value;
            width = regionWidth.Value;
            height = regionHeight.Value;
        }

        (byte[] bgra, int captureWidth, int captureHeight) =
            CaptureRegion(left, top, width, height);

        List<ScreenTextLine> lines;

        try
        {
            lines = OcrService.Read(
                bgra, captureWidth, captureHeight, out _, out _, text, Cancellation);
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
        catch (OperationCanceledException)
        {
            throw new ToolException(
                "E_CANCELLED",
                "OCR 已被取消",
                retryable: true,
                hint: "重新调用 click_text",
                stage: "ocr");
        }

        List<ScreenTextLine> matches = lines
            .Where(line => OcrTextMatches(line.Text, text))
            .ToList();

        if (matches.Count == 0)
        {
            throw new ToolException(
                "E_NO_MATCH",
                $"OCR 未找到文本: {text}",
                new JsonObject
                {
                    ["text"] = text,
                    ["lines"] = lines.Count,
                    ["region"] = RegionJson(left, top, width, height),
                },
                retryable: true,
                hint: "先用 read_screen_text 看 OCR 结果；或缩小 region/查询词",
                stage: "ocr");
        }

        if (requireUnique && matches.Count > 1)
        {
            throw new ToolException(
                "E_MULTIPLE_MATCHES",
                $"OCR 命中 {matches.Count} 处文本，require_unique=true 拒绝点击",
                new JsonObject
                {
                    ["text"] = text,
                    ["matches"] = DescribeMatches(matches, left, top),
                },
                retryable: true,
                hint: "缩小 region、使用更长的查询词，或显式 require_unique=false",
                stage: "ocr");
        }

        ScreenTextLine match = matches
            .OrderByDescending(line => line.Width * line.Height)
            .First();

        int clickX = left + match.Left + match.Width / 2;
        int clickY = top + match.Top + match.Height / 2;
        JsonObject click = Click(
            clickX, clickY, "left", 1, channel, keepForeground, observe, fallbackInput);

        int clickRedactions = 0;
        string clickedText = RedactEmails(match.Text, ref clickRedactions);

        return new JsonObject
        {
            ["found"] = true,
            ["text"] = clickedText,
            ["x"] = clickX,
            ["y"] = clickY,
            ["bounds"] = RegionJson(left + match.Left, top + match.Top, match.Width, match.Height),
            ["ambiguous"] = matches.Count > 1,
            ["matches"] = DescribeMatches(matches, left, top),
            ["method"] = click["method"]?.GetValue<string>(),
            ["observed"] = click["observed"]?.DeepClone(),
            ["privacy_redactions"] = clickRedactions,
        };
    }

    private static JsonArray DescribeMatches(
        List<ScreenTextLine> matches, int originX, int originY)
    {
        var array = new JsonArray();
        int redactions = 0;

        foreach (ScreenTextLine line in matches.Take(8))
        {
            array.Add((JsonNode)new JsonObject
            {
                ["text"] = RedactEmails(line.Text, ref redactions),
                ["bounds"] = RegionJson(
                    originX + line.Left, originY + line.Top, line.Width, line.Height),
            });
        }

        return array;
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
            // OCR 常把英文连字符识别成 “һ/一/ー/·” 等字符；匹配时统一丢弃。
            if (ch is '-' or '_' or '.' or '·' or '—' or '–' or '一' or 'ー' or 'һ')
            {
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
    }
}
