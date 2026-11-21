using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Mcp;
using VScreenPilot.Perception;
using VScreenPilot.Views;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    private const int MaxTemplateBytes = 4 * 1024 * 1024;

    /* ------------------------------------------------------------------ */
    /* 工具：match_visual                                                  */
    /* ------------------------------------------------------------------ */

    public MatchVisualView MatchVisual(JsonElement arguments)
    {
        DisplayInfo display = EnsureEnvironment();
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

            (x, y, width, height) = ParseRegion(regionElement);
        }

        RequireRegionInDisplay(display, x, y, width, height, "match_visual");

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
                    ["region"] = RegionJson(regionX, regionY, regionWidth, regionHeight),
                });
        }

        byte[] bgra = ScreenCapture.CapturePixel(px, py);

        return new MatchVisualView(
            Mode: string.Empty,
            ElapsedMs: 0,
            new PointView(px, py),
            ToHex(bgra, 0),
            new int[] { bgra[2], bgra[1], bgra[0] },
            new OptionalBoundsView(regionX, regionY, null, null),
            Target: null, Tolerance: null, MinPixels: null, MaxAreaRatio: null,
            ExcludeBorder: null, MatchedPixels: null, Count: null, Matches: null,
            Threshold: null, Template: null, Ambiguous: null, ScoreMargin: null);
    }

    private MatchVisualView MatchColor(
        JsonElement arguments, int regionX, int regionY, int width, int height)
    {
        string target = GetString(arguments, "target")
            ?? throw new ToolException("E_INVALID_ARG", "color 模式需要 target=\"#RRGGBB\"");

        if (!TryParseHex(target, out byte r, out byte g, out byte b))
        {
            throw new ToolException("E_INVALID_ARG", $"颜色格式无效: {target}");
        }

        int tolerance = Math.Clamp(GetInt(arguments, "tolerance", 16), 0, 255);
        int maxResults = Math.Clamp(GetInt(arguments, "max_results", 10), 1, 100);
        int minPixels = Math.Max(1, GetInt(arguments, "min_pixels", 4));
        double maxAreaRatio = Math.Clamp(
            GetDouble(arguments, "max_area_ratio", 1.0), 0.01, 1.0);
        bool excludeBorder = GetBool(arguments, "exclude_border", false);

        (byte[] bgra, int capturedWidth, int capturedHeight) =
            CaptureRegion(regionX, regionY, width, height);

        int total = capturedWidth * capturedHeight;
        var mask = ArrayPool<byte>.Shared.Rent(total);

        try
        {
            int matched = 0;

            for (int pixel = 0, offset = 0; pixel < total; pixel++, offset += 4)
            {
                bool hit = Math.Abs(bgra[offset + 2] - r) <= tolerance &&
                           Math.Abs(bgra[offset + 1] - g) <= tolerance &&
                           Math.Abs(bgra[offset] - b) <= tolerance;

                mask[pixel] = hit ? (byte)1 : (byte)0;
                if (hit)
                {
                    matched++;
                }
            }

            if (matched == 0)
            {
                throw new ToolException(
                    "E_NO_MATCH",
                    "指定区域内没有匹配颜色",
                    new JsonObject
                    {
                        ["target"] = target,
                        ["tolerance"] = tolerance,
                        ["region"] = RegionJson(regionX, regionY, width, height),
                    });
            }

            List<ColorComponent> components = ConnectedComponents(
                mask, capturedWidth, capturedHeight, minPixels, () => Cancellation.IsCancellationRequested);

            var selected = components
                .Where(item => item.Pixels >= minPixels)
                .Where(item =>
                {
                    int componentWidth = item.Right - item.Left + 1;
                    int componentHeight = item.Bottom - item.Top + 1;
                    double areaRatio = (double)componentWidth * componentHeight / (capturedWidth * capturedHeight);

                    if (areaRatio > maxAreaRatio)
                    {
                        return false;
                    }

                    if (excludeBorder &&
                        (item.Left <= 0 || item.Top <= 0 ||
                         item.Right >= capturedWidth - 1 || item.Bottom >= capturedHeight - 1))
                    {
                        return false;
                    }

                    return true;
                })
                .OrderByDescending(item => item.Pixels)
                .Take(maxResults)
                .ToList();

            if (selected.Count == 0)
            {
                throw new ToolException(
                    "E_NO_MATCH",
                    $"匹配像素存在，但连通区域都小于 min_pixels={minPixels}",
                    new JsonObject
                    {
                        ["target"] = target,
                        ["matched_pixels"] = matched,
                        ["min_pixels"] = minPixels,
                    });
            }

            var matches = new List<VisualMatchView>();

            foreach (ColorComponent component in selected)
            {
                int w = component.Right - component.Left + 1;
                int h = component.Bottom - component.Top + 1;
                int area = w * h;

                matches.Add(new VisualMatchView(
                    regionX + component.Left,
                    regionY + component.Top,
                    w,
                    h,
                    new PointView(
                        regionX + component.Left + w / 2,
                        regionY + component.Top + h / 2),
                    component.Pixels,
                    Math.Round(area == 0 ? 0 : (double)component.Pixels / area, 4),
                    Score: null,
                    Confidence: 1.0,
                    Scale: null));
            }

            return new MatchVisualView(
                Mode: string.Empty,
                ElapsedMs: 0,
                Point: null, Color: null, Rgb: null,
                new OptionalBoundsView(regionX, regionY, width, height),
                target,
                tolerance,
                minPixels,
                maxAreaRatio,
                excludeBorder,
                matched,
                matches.Count,
                matches,
                Threshold: null, Template: null, Ambiguous: null, ScoreMargin: null);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(mask);
        }
    }

    private MatchVisualView MatchTemplate(
        JsonElement arguments, int regionX, int regionY, int width, int height)
    {
        byte[] png;

        string? target = GetString(arguments, "target");

        if (!string.IsNullOrWhiteSpace(target))
        {
            png = DecodeBase64Png(target);
        }
        else
        {
            string? targetPath = GetString(arguments, "target_path");

            if (string.IsNullOrWhiteSpace(targetPath))
            {
                throw new ToolException(
                    "E_INVALID_ARG", "template 模式需要 target=Base64 PNG 或 target_path=.png");
            }

            string fullPath = _paths.ValidatePngPath(targetPath, "target_path");

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

                png = File.ReadAllBytes(fullPath);
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

        // 先读 IHDR：超大模板在完整解码前就拒绝，避免几十 MB 的临时分配。
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
                    ["region"] = RegionJson(regionX, regionY, width, height),
                });
        }

        PngImage image;

        try
        {
            image = PngReader.Decode(png);
        }
        catch (PngFormatException exception)
        {
            throw new ToolException(
                "E_UNSUPPORTED_IMAGE",
                $"模板图片无法解码: {exception.Message}",
                new JsonObject { ["bytes"] = png.Length });
        }

        double threshold = Math.Clamp(GetDouble(arguments, "threshold", 0.88), 0.3, 1.0);
        int maxResults = Math.Clamp(GetInt(arguments, "max_results", 10), 1, 100);
        bool requireUnique = GetBool(arguments, "require_unique", false);

        (byte[] bgra, int capturedWidth, int capturedHeight) =
            CaptureRegion(regionX, regionY, width, height);

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

        List<VisualMatch> found = TemplateMatcher.MatchMultiScale(
            bgra,
            capturedWidth,
            capturedHeight,
            image,
            threshold,
            maxResults,
            scales,
            () => Cancellation.IsCancellationRequested);

        if (found.Count == 0)
        {
            throw new ToolException(
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
                    ["region"] = RegionJson(regionX, regionY, width, height),
                });
        }

        if (requireUnique && found.Count > 1)
        {
            throw new ToolException(
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
        }

        double scoreMargin = found.Count > 1
            ? Math.Round(found[0].Score - found[1].Score, 4)
            : 1.0;

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

    private static JsonObject RegionJson(int x, int y, int width, int height) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height,
    };

    private static byte[] DecodeBase64Png(string value)
    {
        string payload = value.Trim();

        int comma = payload.IndexOf(',');
        if (payload.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma > 0)
        {
            payload = payload[(comma + 1)..];
        }

        // Base64 字符串会在解码时再分配一份内存；先按上限拒绝，避免超大 target 撑爆进程。
        const int MaxBase64Chars = 8 * 1024 * 1024;

        if (payload.Length > MaxBase64Chars)
        {
            throw new ToolException(
                "E_UNSUPPORTED_IMAGE",
                $"模板 Base64 过大: {payload.Length} 字符（上限 {MaxBase64Chars}）");
        }

        byte[] data;

        try
        {
            data = Convert.FromBase64String(payload);
        }
        catch (FormatException)
        {
            throw new ToolException("E_INVALID_ARG", "target 不是合法的 Base64 数据");
        }

        if (data.Length > MaxTemplateBytes)
        {
            throw new ToolException(
                "E_UNSUPPORTED_IMAGE",
                $"模板解码后过大: {data.Length} 字节（上限 {MaxTemplateBytes}）",
                new JsonObject { ["bytes"] = data.Length });
        }

        return data;
    }

    private readonly record struct ColorComponent(
        int Left, int Top, int Right, int Bottom, int Pixels);

    /// <summary>4 连通域标记：返回连通区域的边界与像素数。</summary>
    private static List<ColorComponent> ConnectedComponents(
        byte[] mask, int width, int height, int minPixels, Func<bool> isCancelled)
    {
        var components = new List<ColorComponent>();
        int total = width * height;
        var visited = ArrayPool<byte>.Shared.Rent(total);
        var queue = ArrayPool<int>.Shared.Rent(total);

        try
        {
            Array.Clear(visited, 0, total);

            for (int start = 0; start < total; start++)
            {
                if ((start & 0xFFFF) == 0 && isCancelled())
                {
                    throw new ToolException("E_CANCELLED", "请求已取消");
                }

                if (mask[start] == 0 || visited[start] != 0)
                {
                    continue;
                }

                int head = 0;
                int tail = 0;
                queue[tail++] = start;
                visited[start] = 1;

                int minX = width, minY = height, maxX = -1, maxY = -1, pixels = 0;

                while (head < tail)
                {
                    int index = queue[head++];
                    int px = index % width;
                    int py = index / width;
                    pixels++;

                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;

                    if (px > 0)
                    {
                        int next = index - 1;
                        if (mask[next] != 0 && visited[next] == 0)
                        {
                            visited[next] = 1;
                            queue[tail++] = next;
                        }
                    }

                    if (px < width - 1)
                    {
                        int next = index + 1;
                        if (mask[next] != 0 && visited[next] == 0)
                        {
                            visited[next] = 1;
                            queue[tail++] = next;
                        }
                    }

                    if (py > 0)
                    {
                        int next = index - width;
                        if (mask[next] != 0 && visited[next] == 0)
                        {
                            visited[next] = 1;
                            queue[tail++] = next;
                        }
                    }

                    if (py < height - 1)
                    {
                        int next = index + width;
                        if (mask[next] != 0 && visited[next] == 0)
                        {
                            visited[next] = 1;
                            queue[tail++] = next;
                        }
                    }
                }

                // 小于阈值的碎片直接丢弃，避免碎片化界面产生海量无用连通域。
                if (pixels >= minPixels)
                {
                    components.Add(new ColorComponent(minX, minY, maxX, maxY, pixels));
                }
            }
        }        finally
        {
            ArrayPool<byte>.Shared.Return(visited);
            ArrayPool<int>.Shared.Return(queue);
        }

        return components;
    }
}
