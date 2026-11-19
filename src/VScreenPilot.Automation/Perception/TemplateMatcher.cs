using VScreenPilot.Perception;

namespace VScreenPilot.Perception;

internal sealed record VisualMatch(
    int X, int Y, int Width, int Height, double Score, double Scale = 1.0);

/// <summary>
/// 纯托管模板匹配：灰度金字塔粗到细 SAD + 非极大值抑制。
/// 输入为虚拟屏 BGRA 位图与 PNG 模板（RGBA）。
/// </summary>
internal static class TemplateMatcher
{
    private const int MaxLevels = 4;
    private const int CandidateLimit = 64;

    public static List<VisualMatch> Match(
        byte[] screenBgra,
        int screenWidth,
        int screenHeight,
        PngImage template,
        double threshold,
        int maxResults,
        Func<bool>? isCancelled)
    {
        if (template.Width > screenWidth || template.Height > screenHeight)
        {
            return new List<VisualMatch>();
        }

        threshold = Math.Clamp(threshold, 0.3, 1.0);
        maxResults = Math.Clamp(maxResults, 1, 100);

        List<GrayLevel> screen = BuildScreenPyramid(screenBgra, screenWidth, screenHeight);
        List<TemplateLevel> templates = BuildTemplatePyramid(template);

        int levels = Math.Min(screen.Count, templates.Count);
        int last = 0;

        for (int level = 1; level < levels; level++)
        {
            if (templates[level].Width < 8 || templates[level].Height < 8)
            {
                break;
            }

            if (templates[level].Width > screen[level].Width ||
                templates[level].Height > screen[level].Height)
            {
                break;
            }

            last = level;
        }

        var candidates = SearchCoarse(screen[last], templates[last], last, threshold, isCancelled);

        if (candidates.Count == 0)
        {
            return new List<VisualMatch>();
        }

        for (int level = last - 1; level >= 0; level--)
        {
            if (isCancelled?.Invoke() == true)
            {
                return new List<VisualMatch>();
            }

            candidates = Refine(screen[level], templates[level], candidates, isCancelled);
        }

        return Suppress(candidates, template.Width, template.Height, threshold, maxResults);
    }

    /// <summary>
    /// 多尺度匹配：DPI/缩放变化时模板尺寸不再 1:1。
    /// 每个尺度独立匹配，最后按 score 合并；命中结果带实际 scale。
    /// </summary>
    public static List<VisualMatch> MatchMultiScale(
        byte[] screenBgra,
        int screenWidth,
        int screenHeight,
        PngImage template,
        double threshold,
        int maxResults,
        IReadOnlyList<double>? scales,
        Func<bool>? isCancelled)
    {
        if (scales is null || scales.Count == 0)
        {
            List<VisualMatch> single = Match(
                screenBgra, screenWidth, screenHeight, template, threshold, maxResults, isCancelled);
            return single.Select(match => match with { Scale = 1.0 }).ToList();
        }

        var combined = new List<VisualMatch>();

        foreach (double raw in scales.Distinct())
        {
            if (isCancelled?.Invoke() == true)
            {
                break;
            }

            double scale = Math.Clamp(raw, 0.5, 2.0);
            PngImage scaled = Math.Abs(scale - 1.0) < 0.001
                ? template
                : Resize(template, scale);

            if (scaled.Width > screenWidth || scaled.Height > screenHeight)
            {
                continue;
            }

            List<VisualMatch> matches = Match(
                screenBgra, screenWidth, screenHeight, scaled, threshold, maxResults, isCancelled);

            foreach (VisualMatch match in matches)
            {
                combined.Add(match with
                {
                    Width = scaled.Width,
                    Height = scaled.Height,
                    Scale = scale,
                });
            }
        }

        var results = new List<VisualMatch>();

        foreach (VisualMatch match in combined.OrderByDescending(item => item.Score))
        {
            // 不同尺度可能命中同一位置，按半宽/半高去重。
            bool duplicate = results.Any(existing =>
                Math.Abs(existing.X - match.X) < Math.Max(2, match.Width / 2) &&
                Math.Abs(existing.Y - match.Y) < Math.Max(2, match.Height / 2));

            if (duplicate)
            {
                continue;
            }

            results.Add(match);

            if (results.Count >= Math.Clamp(maxResults, 1, 100))
            {
                break;
            }
        }

        return results;
    }

    /// <summary>最近邻缩放：模板匹配前会转灰度，最近邻足够且可预测。</summary>
    private static PngImage Resize(PngImage source, double scale)
    {
        int width = Math.Max(1, (int)Math.Round(source.Width * scale));
        int height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var rgba = new byte[width * height * 4];

        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min(source.Height - 1, (int)(y / scale));

            for (int x = 0; x < width; x++)
            {
                int sourceX = Math.Min(source.Width - 1, (int)(x / scale));
                int sourceIndex = (sourceY * source.Width + sourceX) * 4;
                int destination = (y * width + x) * 4;
                rgba[destination] = source.Rgba[sourceIndex];
                rgba[destination + 1] = source.Rgba[sourceIndex + 1];
                rgba[destination + 2] = source.Rgba[sourceIndex + 2];
                rgba[destination + 3] = source.Rgba[sourceIndex + 3];
            }
        }

        return new PngImage(rgba, width, height);
    }

    private static List<Candidate> SearchCoarse(
        GrayLevel screen,
        TemplateLevel template,
        int level,
        double threshold,
        Func<bool>? isCancelled)
    {
        var candidates = new List<Candidate>();
        int maxX = screen.Width - template.Width;
        int maxY = screen.Height - template.Height;

        if (maxX < 0 || maxY < 0)
        {
            return candidates;
        }

        double cutoff = Math.Max(0.35, threshold - 0.08 * level);

        for (int y = 0; y <= maxY; y++)
        {
            if ((y & 15) == 0)
            {
                if (isCancelled?.Invoke() == true)
                {
                    return candidates;
                }
            }

            for (int x = 0; x <= maxX; x++)
            {
                double score = Score(screen, template, x, y, cutoff);
                if (score < 0)
                {
                    continue;
                }

                candidates.Add(new Candidate(x, y, score));

                if (candidates.Count > CandidateLimit * 4)
                {
                    candidates = candidates
                        .OrderByDescending(item => item.Score)
                        .Take(CandidateLimit)
                        .ToList();
                }
            }
        }

        return candidates
            .OrderByDescending(item => item.Score)
            .Take(CandidateLimit)
            .ToList();
    }

    private static List<Candidate> Refine(
        GrayLevel screen, TemplateLevel template, List<Candidate> candidates, Func<bool>? isCancelled)
    {
        var refined = new List<Candidate>(candidates.Count * 3);
        int maxX = screen.Width - template.Width;
        int maxY = screen.Height - template.Height;

        if (maxX < 0 || maxY < 0)
        {
            return refined;
        }

        foreach (Candidate candidate in candidates)
        {
            if (isCancelled?.Invoke() == true)
            {
                return refined;
            }

            int baseX = candidate.X * 2;
            int baseY = candidate.Y * 2;
            double bestScore = -1;
            int bestX = baseX;
            int bestY = baseY;

            for (int dy = -2; dy <= 2; dy++)
            {
                int y = Math.Clamp(baseY + dy, 0, maxY);

                for (int dx = -2; dx <= 2; dx++)
                {
                    int x = Math.Clamp(baseX + dx, 0, maxX);
                    double score = Score(screen, template, x, y, -1);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestX = x;
                        bestY = y;
                    }
                }
            }

            if (bestScore >= 0)
            {
                refined.Add(new Candidate(bestX, bestY, bestScore));
            }
        }

        return refined;
    }

    private static double Score(
        GrayLevel screen, TemplateLevel template, int x, int y, double cutoff)
    {
        int limit = cutoff < 0
            ? int.MaxValue
            : (int)((1.0 - cutoff) * template.MaskCount * 255.0);

        long sad = 0;
        int count = 0;

        for (int ty = 0; ty < template.Height; ty++)
        {
            int screenRow = (y + ty) * screen.Width + x;
            int templateRow = ty * template.Width;

            for (int tx = 0; tx < template.Width; tx++)
            {
                if (template.Mask[templateRow + tx] == 0)
                {
                    continue;
                }

                int difference = screen.Data[screenRow + tx] - template.Gray[templateRow + tx];
                sad += difference < 0 ? -difference : difference;
                count++;

                if (sad > limit)
                {
                    return -1;
                }
            }
        }

        return count == 0 ? 0 : 1.0 - (double)sad / (count * 255.0);
    }

    private static List<VisualMatch> Suppress(
        List<Candidate> candidates,
        int width,
        int height,
        double threshold,
        int maxResults)
    {
        var ordered = candidates
            .Where(item => item.Score >= threshold)
            .OrderByDescending(item => item.Score)
            .ToList();

        var results = new List<VisualMatch>();
        int minDistanceX = Math.Max(2, width / 2);
        int minDistanceY = Math.Max(2, height / 2);

        foreach (Candidate candidate in ordered)
        {
            bool suppressed = results.Any(match =>
                Math.Abs(match.X - candidate.X) < minDistanceX &&
                Math.Abs(match.Y - candidate.Y) < minDistanceY);

            if (suppressed)
            {
                continue;
            }

            results.Add(new VisualMatch(
                candidate.X, candidate.Y, width, height, Math.Round(candidate.Score, 4)));

            if (results.Count >= maxResults)
            {
                break;
            }
        }

        return results;
    }

    private static List<GrayLevel> BuildScreenPyramid(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];

        for (int index = 0, pixel = 0; pixel < gray.Length; pixel++, index += 4)
        {
            // BGRA: 蓝 0、绿 1、红 2
            gray[pixel] = (byte)((bgra[index + 2] * 77 + bgra[index + 1] * 150 + bgra[index] * 29) >> 8);
        }

        var levels = new List<GrayLevel> { new(gray, width, height) };
        var current = levels[0];

        for (int level = 1; level < MaxLevels; level++)
        {
            if (current.Width < 16 || current.Height < 16)
            {
                break;
            }

            GrayLevel next = Downsample(current);
            levels.Add(next);
            current = next;
        }

        return levels;
    }

    private static List<TemplateLevel> BuildTemplatePyramid(PngImage template)
    {
        var levels = new List<TemplateLevel>
        {
            BuildTemplateLevel(template.Rgba, template.Width, template.Height),
        };

        TemplateLevel current = levels[0];

        for (int level = 1; level < MaxLevels; level++)
        {
            if (current.Width < 16 || current.Height < 16)
            {
                break;
            }

            TemplateLevel next = Downsample(current);
            levels.Add(next);
            current = next;
        }

        return levels;
    }

    private static TemplateLevel BuildTemplateLevel(byte[] rgba, int width, int height)
    {
        var gray = new byte[width * height];
        var mask = new byte[width * height];
        int maskCount = 0;

        for (int pixel = 0; pixel < gray.Length; pixel++)
        {
            int index = pixel * 4;
            gray[pixel] = (byte)((rgba[index] * 77 + rgba[index + 1] * 150 + rgba[index + 2] * 29) >> 8);

            if (rgba[index + 3] >= 16)
            {
                mask[pixel] = 1;
                maskCount++;
            }
        }

        // 全透明模板视为无掩码，避免 maskCount=0 无法匹配。
        if (maskCount == 0)
        {
            Array.Fill(mask, (byte)1);
            maskCount = mask.Length;
        }

        return new TemplateLevel(gray, mask, width, height, maskCount);
    }

    private static GrayLevel Downsample(GrayLevel source)
    {
        int width = Math.Max(1, source.Width / 2);
        int height = Math.Max(1, source.Height / 2);
        var data = new byte[width * height];

        for (int y = 0; y < height; y++)
        {
            int y0 = y * 2;
            int y1 = Math.Min(y0 + 1, source.Height - 1);

            for (int x = 0; x < width; x++)
            {
                int x0 = x * 2;
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                int sum = source.Data[y0 * source.Width + x0] +
                          source.Data[y0 * source.Width + x1] +
                          source.Data[y1 * source.Width + x0] +
                          source.Data[y1 * source.Width + x1];
                data[y * width + x] = (byte)(sum / 4);
            }
        }

        return new GrayLevel(data, width, height);
    }

    private static TemplateLevel Downsample(TemplateLevel source)
    {
        int width = Math.Max(1, source.Width / 2);
        int height = Math.Max(1, source.Height / 2);
        var gray = new byte[width * height];
        var mask = new byte[width * height];
        int maskCount = 0;

        for (int y = 0; y < height; y++)
        {
            int y0 = y * 2;
            int y1 = Math.Min(y0 + 1, source.Height - 1);

            for (int x = 0; x < width; x++)
            {
                int x0 = x * 2;
                int x1 = Math.Min(x0 + 1, source.Width - 1);
                int sum = 0;
                int count = 0;
                int anyMask = 0;

                for (int sy = y0; sy <= y1; sy++)
                {
                    for (int sx = x0; sx <= x1; sx++)
                    {
                        int index = sy * source.Width + sx;
                        if (source.Mask[index] == 0)
                        {
                            continue;
                        }

                        anyMask = 1;
                        sum += source.Gray[index];
                        count++;
                    }
                }

                int destination = y * width + x;
                gray[destination] = count == 0 ? (byte)0 : (byte)(sum / count);
                mask[destination] = (byte)anyMask;

                if (anyMask != 0)
                {
                    maskCount++;
                }
            }
        }

        return new TemplateLevel(gray, mask, width, height, Math.Max(1, maskCount));
    }

    private readonly record struct Candidate(int X, int Y, double Score);

    private sealed record GrayLevel(byte[] Data, int Width, int Height);

    private sealed record TemplateLevel(
        byte[] Gray, byte[] Mask, int Width, int Height, int MaskCount);
}
