using VScreenPilot.Perception;

namespace VScreenPilot.Perception;

/// <summary>灰度金字塔与模板层构建；从 TemplateMatcher 拆出。</summary>
internal static class TemplatePyramid
{
    internal static List<GrayLevel> BuildScreenPyramid(byte[] bgra, int width, int height)
    {
        var gray = new byte[width * height];

        for (int index = 0, pixel = 0; pixel < gray.Length; pixel++, index += 4)
        {
            // BGRA: 蓝 0、绿 1、红 2
            gray[pixel] = (byte)((bgra[index + 2] * 77 + bgra[index + 1] * 150 + bgra[index] * 29) >> 8);
        }

        var levels = new List<GrayLevel> { new(gray, width, height) };
        var current = levels[0];

        for (int level = 1; level < TemplateMatcher.MaxLevels; level++)
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

    internal static List<TemplateLevel> BuildTemplatePyramid(PngImage template)
    {
        var levels = new List<TemplateLevel>
        {
            BuildTemplateLevel(template.Rgba, template.Width, template.Height),
        };

        TemplateLevel current = levels[0];

        for (int level = 1; level < TemplateMatcher.MaxLevels; level++)
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

    internal static TemplateLevel BuildTemplateLevel(byte[] rgba, int width, int height)
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

    internal static GrayLevel Downsample(GrayLevel source)
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

    internal static TemplateLevel Downsample(TemplateLevel source)
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

    internal readonly record struct Candidate(int X, int Y, double Score);


    internal sealed record GrayLevel(byte[] Data, int Width, int Height);


    internal sealed record TemplateLevel(
        byte[] Gray, byte[] Mask, int Width, int Height, int MaskCount);
}
