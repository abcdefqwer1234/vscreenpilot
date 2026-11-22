using System.Diagnostics;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Json;
using VScreenPilot.Perception;
using VScreenPilot.Views;
using VScreenPilot.Errors;

using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Session;

/// <summary>OCR 工具所需的最小宿主接口。</summary>
internal sealed record ScreenTextContext(
    Func<DisplayInfo> EnsureDisplay,
    Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> CaptureRegion,
    Func<CancellationToken> Cancellation,
    Action RecordOcr,
    Func<SessionClickRequest, ClickView> Click);

/// <summary>read_screen_text / click_text：OCR 读取、匹配与按文本点击。</summary>
internal sealed class ScreenTextService
{
    private readonly ScreenTextContext _host;
    private readonly OcrService _ocr = new();

    private sealed record ClickRegion(int Left, int Top, int Width, int Height);

    public ScreenTextService(ScreenTextContext host) => _host = host;

    /// <summary>
    /// OCR 读取屏幕文本。坐标返回的是虚拟屏绝对坐标，可直接用于 click。
    /// </summary>
    public ScreenTextView ReadScreenText(
        int? x, int? y, int? width, int? height, string? query, int maxLines)
    {
        DisplayInfo display = _host.EnsureDisplay();
        var stopwatch = Stopwatch.StartNew();
        int left = display.X;
        int top = display.Y;
        int regionWidth = display.Width;
        int regionHeight = display.Height;

        if (x is not null && y is not null && width is not null && height is not null)
        {
            VisualSupport.RequireRegionInDisplay(display, x.Value, y.Value, width.Value, height.Value, "read_screen_text");
            left = x.Value;
            top = y.Value;
            regionWidth = width.Value;
            regionHeight = height.Value;
        }

        (byte[] bgra, int captureWidth, int captureHeight) =
            _host.CaptureRegion(left, top, regionWidth, regionHeight);

        List<ScreenTextLine> lines;

        try
        {
            lines = _ocr.Read(
                bgra, captureWidth, captureHeight,
                out string engine, out string language, query ?? string.Empty,
                _host.Cancellation());
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

    private static ScreenTextView BuildTextResult(
        List<ScreenTextLine> lines,
        int originX, int originY, int width, int height,
        string? query, int maxLines,
        string engine, string language,
        Stopwatch stopwatch)
    {
        var all = new List<ScreenTextLineView>();
        var matches = new List<ScreenTextLineView>();
        int limit = Math.Clamp(maxLines, 1, 200);
        int redactions = 0;

        foreach (ScreenTextLine line in lines)
        {
            string redacted = PrivacyRedactor.RedactEmails(line.Text, ref redactions);

            var item = new ScreenTextLineView(
                redacted,
                new BoundsView(
                    originX + line.Left,
                    originY + line.Top,
                    line.Width,
                    line.Height));

            if (!string.IsNullOrWhiteSpace(query) &&
                OcrTextMatches(line.Text, query!))
            {
                matches.Add(item);
            }

            if (all.Count < limit)
            {
                all.Add(item);
            }
        }

        return new ScreenTextView(
            engine,
            language,
            all,
            matches,
            lines.Count,
            all.Count,
            lines.Count > all.Count,
            redactions,
            new BoundsView(originX, originY, width, height),
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1));
    }

    /// <summary>OCR 定位 + 点击 + 前后差异，供 click_ref 找不到元素的画布应用使用。</summary>
    public ClickTextView ClickText(
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

        ClickRegion region = ResolveClickRegion(regionX, regionY, regionWidth, regionHeight);
        int left = region.Left;
        int top = region.Top;
        int width = region.Width;
        int height = region.Height;

        List<ScreenTextLine> lines = RunOcr(region, text);

        List<ScreenTextLine> matches = lines
            .Where(line => OcrTextMatches(line.Text, text))
            .ToList();
        ScreenTextLine match = SelectClickMatch(matches, text, requireUnique, region, lines.Count);

        int clickX = left + match.Left + match.Width / 2;
        int clickY = top + match.Top + match.Height / 2;
        ClickView click = _host.Click(new SessionClickRequest(
            clickX, clickY, "left", 1, channel, keepForeground, observe, fallbackInput));

        int clickRedactions = 0;
        string clickedText = PrivacyRedactor.RedactEmails(match.Text, ref clickRedactions);

        return new ClickTextView(
            true,
            clickedText,
            clickX,
            clickY,
            new BoundsView(left + match.Left, top + match.Top, match.Width, match.Height),
            matches.Count > 1,
            DescribeMatches(matches, region.Left, region.Top),
            click.Method,
            click.Observed,
            clickRedactions);
    }

    private ClickRegion ResolveClickRegion(
        int? regionX, int? regionY, int? regionWidth, int? regionHeight)
    {
        DisplayInfo display = _host.EnsureDisplay();
        int left = display.X;
        int top = display.Y;
        int width = display.Width;
        int height = display.Height;

        if (regionX is not null && regionY is not null &&
            regionWidth is not null && regionHeight is not null)
        {
            VisualSupport.RequireRegionInDisplay(
                display, regionX.Value, regionY.Value, regionWidth.Value, regionHeight.Value,
                "click_text");
            left = regionX.Value;
            top = regionY.Value;
            width = regionWidth.Value;
            height = regionHeight.Value;
        }

        return new ClickRegion(left, top, width, height);
    }

    private List<ScreenTextLine> RunOcr(ClickRegion region, string text)
    {
        (byte[] bgra, int captureWidth, int captureHeight) =
            _host.CaptureRegion(region.Left, region.Top, region.Width, region.Height);

        try
        {
            List<ScreenTextLine> lines = _ocr.Read(
                bgra, captureWidth, captureHeight, out _, out _, text, _host.Cancellation());
            _host.RecordOcr();
            return lines;
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
    }

    private static ScreenTextLine SelectClickMatch(
        List<ScreenTextLine> matches, string text, bool requireUnique, ClickRegion region, int totalLines)
    {

        if (matches.Count == 0)
        {
            throw new ToolException(
                "E_NO_MATCH",
                $"OCR 未找到文本: {text}",
                new JsonObject
                {
                    ["text"] = text,
                    ["lines"] = totalLines,
                    ["region"] = VisualSupport.RegionJson(region.Left, region.Top, region.Width, region.Height),
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
                    ["matches"] = VspJson.ToNode(DescribeMatches(matches, region.Left, region.Top)),
                },
                retryable: true,
                hint: "缩小 region、使用更长的查询词，或显式 require_unique=false",
                stage: "ocr");
        }

        return matches
            .OrderByDescending(line => line.Width * line.Height)
            .First();
    }

    private static List<ScreenTextLineView> DescribeMatches(
        List<ScreenTextLine> matches, int originX, int originY)
    {
        var array = new List<ScreenTextLineView>();
        int redactions = 0;

        foreach (ScreenTextLine line in matches.Take(8))
        {
            array.Add(new ScreenTextLineView(
                PrivacyRedactor.RedactEmails(line.Text, ref redactions),
                new BoundsView(
                    originX + line.Left, originY + line.Top, line.Width, line.Height)));
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
