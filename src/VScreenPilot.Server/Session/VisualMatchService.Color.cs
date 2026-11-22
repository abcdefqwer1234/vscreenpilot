using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Perception;
using VScreenPilot.Mcp;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Views;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

internal sealed partial class VisualMatchService
{
    private MatchVisualView MatchColor(
        JsonElement arguments, int regionX, int regionY, int width, int height)
    {
        string target = GetString(arguments, "target")
            ?? throw new ToolException("E_INVALID_ARG", "color 模式需要 target=\"#RRGGBB\"");

        if (!VisualSupport.TryParseHex(target, out byte r, out byte g, out byte b))
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
            _host.CaptureRegion(regionX, regionY, width, height);

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
                        ["region"] = VisualSupport.RegionJson(regionX, regionY, width, height),
                    });
            }

            List<ColorComponent> components = ConnectedComponents(
                mask, capturedWidth, capturedHeight, minPixels, () => _host.IsCancelled());

            List<ColorComponent> selected = SelectComponents(
                components, minPixels, maxAreaRatio, excludeBorder,
                capturedWidth, capturedHeight, maxResults);

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

            List<VisualMatchView> matches = BuildColorViews(selected, regionX, regionY);

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


    private static List<ColorComponent> SelectComponents(
        List<ColorComponent> components, int minPixels, double maxAreaRatio,
        bool excludeBorder, int capturedWidth, int capturedHeight, int maxResults)
    {
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

        return selected;
    }

    private static List<VisualMatchView> BuildColorViews(
        List<ColorComponent> selected, int regionX, int regionY)
    {
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

        return matches;
    }
}
