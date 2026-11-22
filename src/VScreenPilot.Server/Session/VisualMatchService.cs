using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Perception;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Views;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

/// <summary>match_visual 所需的最小宿主接口。</summary>
internal sealed record VisualContext(
    Func<DisplayInfo> EnsureDisplay,
    Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> CaptureRegion,
    Func<string, string, string> ValidatePngPath,
    Func<bool> IsCancelled);

/// <summary>match_visual：像素 / 颜色连通域 / 模板多尺度匹配。</summary>
internal sealed partial class VisualMatchService
{
    private readonly VisualContext _host;

    public VisualMatchService(VisualContext host) => _host = host;

    private const int MaxTemplateBytes = 4 * 1024 * 1024;

    /* ------------------------------------------------------------------ */
    /* 工具：match_visual                                                  */
    /* ------------------------------------------------------------------ */

    public MatchVisualView Match(JsonElement arguments)
    {
        DisplayInfo display = _host.EnsureDisplay();
        string mode = (GetString(arguments, "mode") ?? "color").ToLowerInvariant();

        int x = display.X;
        int y = display.Y;
        int width = display.Width;
        int height = display.Height;

        if (arguments.TryGetProperty("region", out JsonElement regionElement))
        {
            if (regionElement.ValueKind != JsonValueKind.Object)
            {
                throw new ToolException("E_INVALID_ARG", "region 必须是对象");
            }

            (x, y, width, height) = VisualSupport.ParseRegion(regionElement);
        }

        VisualSupport.RequireRegionInDisplay(display, x, y, width, height, "match_visual");

        var stopwatch = Stopwatch.StartNew();

        MatchVisualView result = mode switch
        {
            "pixel" => MatchPixel(arguments, x, y, width, height),
            "color" => MatchColor(arguments, x, y, width, height),
            "template" => MatchTemplate(arguments, x, y, width, height),
            _ => throw new ToolException(
                "E_INVALID_ARG", $"未知 match_visual mode: {mode}（可选 template/color/pixel）"),
        };

        return result with
        {
            Mode = mode,
            ElapsedMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
        };
    }

    private MatchVisualView MatchPixel(
        JsonElement arguments, int regionX, int regionY, int regionWidth, int regionHeight)
    {
        if (!arguments.TryGetProperty("target", out JsonElement target) ||
            target.ValueKind != JsonValueKind.Object)
        {
            throw new ToolException("E_INVALID_ARG", "pixel 模式需要 target={x,y}");
        }

        int px = GetInt(target, "x", int.MinValue);
        int py = GetInt(target, "y", int.MinValue);

        if (px == int.MinValue || py == int.MinValue)
        {
            throw new ToolException("E_INVALID_ARG", "pixel target 需要整数 x 与 y");
        }

        if (px < regionX || py < regionY ||
            px >= (long)regionX + regionWidth || py >= (long)regionY + regionHeight)
        {
            throw new ToolException(
                "E_OUT_OF_BOUNDS",
                "pixel 坐标不在搜索区域内，拒绝读取主屏",
                new JsonObject
                {
                    ["point"] = new JsonObject { ["x"] = px, ["y"] = py },
                    ["region"] = VisualSupport.RegionJson(regionX, regionY, regionWidth, regionHeight),
                });
        }

        byte[] bgra = _host.CaptureRegion(px, py, 1, 1).Bgra;

        return new MatchVisualView(
            Mode: string.Empty,
            ElapsedMs: 0,
            new PointView(px, py),
            VisualSupport.ToHex(bgra, 0),
            new int[] { bgra[2], bgra[1], bgra[0] },
            new OptionalBoundsView(regionX, regionY, null, null),
            Target: null, Tolerance: null, MinPixels: null, MaxAreaRatio: null,
            ExcludeBorder: null, MatchedPixels: null, Count: null, Matches: null,
            Threshold: null, Template: null, Ambiguous: null, ScoreMargin: null);
    }

    private MatchVisualView MatchTemplate(
        JsonElement arguments, int regionX, int regionY, int width, int height)
    {
        byte[] png = LoadTemplate(arguments);
        PngImage image = ReadTemplateImage(png, regionX, regionY, width, height);

        double threshold = Math.Clamp(GetDouble(arguments, "threshold", 0.88), 0.3, 1.0);
        int maxResults = Math.Clamp(GetInt(arguments, "max_results", 10), 1, 100);
        bool requireUnique = GetBool(arguments, "require_unique", false);

        (byte[] bgra, int capturedWidth, int capturedHeight) =
            _host.CaptureRegion(regionX, regionY, width, height);

        List<double> scales = ParseScales(arguments);

        List<VisualMatch> found = TemplateMatcher.MatchMultiScale(
            bgra,
            capturedWidth,
            capturedHeight,
            image,
            threshold,
            maxResults,
            scales,
            () => _host.IsCancelled());

        if (found.Count == 0)
        {
            throw NoMatch(threshold, image, regionX, regionY, width, height);
        }

        if (requireUnique && found.Count > 1)
        {
            throw MultipleMatches(found, regionX, regionY);
        }

        double scoreMargin = found.Count > 1
            ? Math.Round(found[0].Score - found[1].Score, 4)
            : 1.0;

        List<VisualMatchView> matches = BuildMatches(found, regionX, regionY);

        return new MatchVisualView(
            Mode: string.Empty,
            ElapsedMs: 0,
            Point: null, Color: null, Rgb: null,
            new OptionalBoundsView(regionX, regionY, width, height),
            Target: null, Tolerance: null, MinPixels: null, MaxAreaRatio: null,
            ExcludeBorder: null, MatchedPixels: null,
            matches.Count,
            matches,
            threshold,
            new TemplateSizeView(image.Width, image.Height),
            found.Count > 1,
            scoreMargin);
    }

    private byte[] LoadTemplate(JsonElement arguments)
    {
        string? target = GetString(arguments, "target");

        if (!string.IsNullOrWhiteSpace(target))
        {
            return DecodeBase64Png(target);
        }

        string? targetPath = GetString(arguments, "target_path");

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ToolException(
                "E_INVALID_ARG", "template 模式需要 target=Base64 PNG 或 target_path=.png");
        }

        string fullPath = _host.ValidatePngPath(targetPath, "target_path");

        try
        {
            var file = new FileInfo(fullPath);

            if (!file.Exists)
            {
                throw new ToolException(
                    "E_INVALID_ARG", $"模板文件不存在: {fullPath}");
            }

            if (file.Length > MaxTemplateBytes)
            {
                throw new ToolException(
                    "E_UNSUPPORTED_IMAGE",
                    $"模板文件过大: {file.Length} 字节（上限 {MaxTemplateBytes}）",
                    new JsonObject { ["path"] = fullPath, ["bytes"] = file.Length });
            }

            return File.ReadAllBytes(fullPath);
        }
        catch (ToolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ToolException("E_IO", $"读取模板失败: {exception.Message}");
        }
    }

    /// <summary>先读 IHDR：超大模板在完整解码前就拒绝，避免几十 MB 的临时分配。</summary>
    private static PngImage ReadTemplateImage(byte[] png, int regionX, int regionY, int width, int height)
    {
        PngInfo info;

        try
        {
            info = PngReader.ReadInfo(png);
        }
        catch (PngFormatException exception)
        {
            throw new ToolException(
                "E_UNSUPPORTED_IMAGE",
                $"模板图片无法解码: {exception.Message}",
                new JsonObject { ["bytes"] = png.Length });
        }

        if (info.Width > width || info.Height > height)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"模板尺寸 {info.Width}x{info.Height} 大于搜索区域 {width}x{height}",
                new JsonObject
                {
                    ["template"] = new JsonObject
                    {
                        ["width"] = info.Width,
                        ["height"] = info.Height,
                    },
                    ["region"] = VisualSupport.RegionJson(regionX, regionY, width, height),
                });
        }

        try
        {
            return PngReader.Decode(png);
        }
        catch (PngFormatException exception)
        {
            throw new ToolException(
                "E_UNSUPPORTED_IMAGE",
                $"模板图片无法解码: {exception.Message}",
                new JsonObject { ["bytes"] = png.Length });
        }
    }

    private static List<double> ParseScales(JsonElement arguments)
    {
        var scales = new List<double>();

        if (arguments.ValueKind == JsonValueKind.Object &&
            arguments.TryGetProperty("scales", out JsonElement scalesElement) &&
            scalesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in scalesElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Number && item.TryGetDouble(out double value))
                {
                    scales.Add(Math.Clamp(value, 0.5, 2.0));
                }
            }
        }

        if (scales.Count == 0)
        {
            scales.Add(1.0);
        }

        return scales;
    }

    private static ToolException NoMatch(
        double threshold, PngImage image, int regionX, int regionY, int width, int height) =>
        new(
            "E_NO_MATCH",
            "模板在指定区域内未达到阈值",
            new JsonObject
            {
                ["threshold"] = threshold,
                ["template"] = new JsonObject
                {
                    ["width"] = image.Width,
                    ["height"] = image.Height,
                },
                ["region"] = VisualSupport.RegionJson(regionX, regionY, width, height),
            });

    private static ToolException MultipleMatches(
        List<VisualMatch> found, int regionX, int regionY) =>
        new(
            "E_MULTIPLE_MATCHES",
            "模板命中多个位置；请缩小 region、提高 threshold，或使用 require_unique=false",
            new JsonObject
            {
                ["count"] = found.Count,
                ["matches"] = new JsonArray(found.Take(5).Select(match => (JsonNode)new JsonObject
                {
                    ["x"] = regionX + match.X,
                    ["y"] = regionY + match.Y,
                    ["score"] = match.Score,
                }).ToArray()),
            });

    private static List<VisualMatchView> BuildMatches(
        List<VisualMatch> found, int regionX, int regionY)
    {
        var matches = new List<VisualMatchView>();

        foreach (VisualMatch match in found)
        {
            matches.Add(new VisualMatchView(
                regionX + match.X,
                regionY + match.Y,
                match.Width,
                match.Height,
                new PointView(
                    regionX + match.X + match.Width / 2,
                    regionY + match.Y + match.Height / 2),
                Pixels: null, Coverage: null,
                match.Score,
                match.Score,
                match.Scale));
        }

        return matches;
    }


}
