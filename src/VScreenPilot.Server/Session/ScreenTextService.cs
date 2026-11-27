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

/// <summary>截图的绝对区域。</summary>
internal readonly record struct CaptureRegion(int Left, int Top, int Width, int Height);

/// <summary>read_screen_text 的请求。</summary>
internal sealed record OcrReadRequest(
    int? X, int? Y, int? Width, int? Height, string? Query, int MaxLines, int Upscale = 0,
    bool OnOutOfBoundsClip = true);

/// <summary>click_text 的行为选项。</summary>
internal sealed record TextClickOptions(
    string Channel, bool KeepForeground, bool Observe, bool RequireUnique, bool FallbackInput,
    bool AllowDestructive = false);

/// <summary>click_text 的请求。</summary>
internal sealed record TextClickRequest(
    string Text, OcrReadRequest Region, TextClickOptions Options);

/// <summary>OCR 结果视图的构建参数。</summary>
internal sealed record TextReport(
    List<ScreenTextLine> Lines,
    CaptureRegion Capture,
    OcrReadRequest Request,
    string Engine,
    string Language,
    Stopwatch Watch,
    int Upscale,
    bool Clipped,
    int OcrWidth,
    int OcrHeight);

/// <summary>read_screen_text / click_text：OCR 读取、匹配与按文本点击。</summary>
internal sealed class ScreenTextService
{
    private readonly ScreenTextContext _host;
    private readonly OcrService _ocr = new();

    public ScreenTextService(ScreenTextContext host) => _host = host;

    /// <summary>
    /// OCR 读取屏幕文本。坐标返回的是虚拟屏绝对坐标，可直接用于 click。
    /// </summary>
    public ScreenTextView ReadScreenText(OcrReadRequest request)
    {
        DisplayInfo display = _host.EnsureDisplay();
        var stopwatch = Stopwatch.StartNew();
        CaptureRegion region = ResolveReadRegion(display, request, out bool clipped);

        (byte[] bgra, int captureWidth, int captureHeight) =
            _host.CaptureRegion(region.Left, region.Top, region.Width, region.Height);

        try
        {
            int upscale = ResolveUpscale(request.Upscale, captureWidth, captureHeight);
            (byte[] ocrBgra, int ocrWidth, int ocrHeight) =
                BitmapOps.Upscale(bgra, captureWidth, captureHeight, upscale);
            List<ScreenTextLine> lines = _ocr.Read(
                new OcrBitmap(ocrBgra, ocrWidth, ocrHeight),
                request.Query ?? string.Empty,
                _host.Cancellation(),
                out string engine,
                out string language);

            if (upscale > 1)
            {
                lines = ScaleLines(lines, upscale);
            }

            return BuildTextResult(new TextReport(
                lines,
                new CaptureRegion(region.Left, region.Top, captureWidth, captureHeight),
                request,
                engine,
                language,
                stopwatch,
                upscale,
                clipped,
                ocrWidth,
                ocrHeight));
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

    private static int ResolveUpscale(int requested, int width, int height)
    {
        int factor = requested is >= 1 and <= 4
            ? requested
            : Math.Min(width, height) switch
            {
                <= 160 => 4,
                <= 300 => 3,
                <= 600 => 2,
                _ => 1,
            };

        while (factor > 1 && (long)width * height * factor * factor > 16_000_000)
        {
            factor--;
        }

        return Math.Max(1, factor);
    }

    internal static List<ScreenTextLine> ScaleLines(List<ScreenTextLine> lines, int factor) =>
        lines.Select(line => new ScreenTextLine(
            line.Text,
            line.Left / factor,
            line.Top / factor,
            Math.Max(1, line.Width / factor),
            Math.Max(1, line.Height / factor))).ToList();

    private static CaptureRegion ResolveReadRegion(
        DisplayInfo display, OcrReadRequest request, out bool clipped)
    {
        clipped = false;

        if (request.X is not null && request.Y is not null &&
            request.Width is not null && request.Height is not null)
        {
            if (!request.OnOutOfBoundsClip)
            {
                VisualSupport.RequireRegionInDisplay(
                    display, request.X.Value, request.Y.Value, request.Width.Value, request.Height.Value,
                    "read_screen_text");
                return new CaptureRegion(
                    request.X.Value, request.Y.Value, request.Width.Value, request.Height.Value);
            }

            var region = VisualSupport.ClipRegionToDisplay(
                display, request.X.Value, request.Y.Value, request.Width.Value, request.Height.Value,
                "read_screen_text");
            clipped = region.Clipped;
            return new CaptureRegion(region.X, region.Y, region.Width, region.Height);
        }

        return new CaptureRegion(display.X, display.Y, display.Width, display.Height);
    }

    private static ScreenTextView BuildTextResult(TextReport report)
    {
        var all = new List<ScreenTextLineView>();
        var matches = new List<ScreenTextLineView>();
        int limit = Math.Clamp(report.Request.MaxLines, 1, 200);
        int redactions = 0;
        string? query = report.Request.Query;

        foreach (ScreenTextLine line in report.Lines)
        {
            string redacted = PrivacyRedactor.RedactEmails(line.Text, ref redactions);

            var item = new ScreenTextLineView(
                redacted,
                new BoundsView(
                    report.Capture.Left + line.Left,
                    report.Capture.Top + line.Top,
                    line.Width,
                    line.Height));

            if (!string.IsNullOrWhiteSpace(query) && OcrTextMatches(line.Text, query!))
            {
                matches.Add(item);
            }

            if (all.Count < limit)
            {
                all.Add(item);
            }
        }

        return new ScreenTextView(
            report.Engine,
            report.Language,
            all,
            matches,
            report.Lines.Count,
            all.Count,
            report.Lines.Count > all.Count,
            redactions,
            new BoundsView(report.Capture.Left, report.Capture.Top, report.Capture.Width, report.Capture.Height),
            Math.Round(report.Watch.Elapsed.TotalMilliseconds, 1),
            report.Upscale,
            report.Clipped,
            report.Capture.Width,
            report.Capture.Height,
            report.OcrWidth,
            report.OcrHeight);
    }

    public ClickTextView ClickText(TextClickRequest request)
    {
        string text = request.Text;
        TextClickOptions options = request.Options;

        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ToolException("E_INVALID_ARG", "click_text 需要非空 text");
        }

        CaptureRegion region = ResolveCaptureRegion(
            request.Region.X, request.Region.Y, request.Region.Width, request.Region.Height,
            request.Region.OnOutOfBoundsClip, out bool clipped);
        int left = region.Left;
        int top = region.Top;
        int width = region.Width;
        int height = region.Height;
        int upscale = ResolveUpscale(request.Region.Upscale, width, height);

        List<ScreenTextLine> lines = RunOcr(region, text, upscale);

        List<ScreenTextLine> matches = lines
            .Where(line => OcrTextMatches(line.Text, text))
            .ToList();
        ScreenTextLine match = SelectClickMatch(matches, text, options.RequireUnique, region, lines.Count);

        int clickX = left + match.Left + match.Width / 2;
        int clickY = top + match.Top + match.Height / 2;
        ClickView click = _host.Click(new SessionClickRequest(
            clickX, clickY, "left", 1, options.Channel,
            options.KeepForeground, options.Observe, options.FallbackInput,
            AllowDestructive: options.AllowDestructive));

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
            clickRedactions,
            click.HitElement,
            upscale,
            clipped);
    }

    private CaptureRegion ResolveCaptureRegion(
        int? regionX, int? regionY, int? regionWidth, int? regionHeight,
        bool clip, out bool clipped)
    {
        clipped = false;
        DisplayInfo display = _host.EnsureDisplay();
        int left = display.X;
        int top = display.Y;
        int width = display.Width;
        int height = display.Height;

        if (regionX is not null && regionY is not null &&
            regionWidth is not null && regionHeight is not null)
        {
            if (clip)
            {
                var region = VisualSupport.ClipRegionToDisplay(
                    display, regionX.Value, regionY.Value, regionWidth.Value, regionHeight.Value,
                    "click_text");
                left = region.X;
                top = region.Y;
                width = region.Width;
                height = region.Height;
                clipped = region.Clipped;
            }
            else
            {
                VisualSupport.RequireRegionInDisplay(
                    display, regionX.Value, regionY.Value, regionWidth.Value, regionHeight.Value,
                    "click_text");
                left = regionX.Value;
                top = regionY.Value;
                width = regionWidth.Value;
                height = regionHeight.Value;
            }
        }

        return new CaptureRegion(left, top, width, height);
    }

    private List<ScreenTextLine> RunOcr(CaptureRegion region, string text, int upscale)
    {
        (byte[] bgra, int captureWidth, int captureHeight) =
            _host.CaptureRegion(region.Left, region.Top, region.Width, region.Height);

        try
        {
            (byte[] ocrBgra, int ocrWidth, int ocrHeight) =
                BitmapOps.Upscale(bgra, captureWidth, captureHeight, upscale);
            List<ScreenTextLine> lines = _ocr.Read(
                new OcrBitmap(ocrBgra, ocrWidth, ocrHeight), text, _host.Cancellation(), out _, out _);
            _host.RecordOcr();
            return upscale > 1 ? ScaleLines(lines, upscale) : lines;
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
        List<ScreenTextLine> matches, string text, bool requireUnique, CaptureRegion region, int totalLines)
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
