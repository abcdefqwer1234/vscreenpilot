namespace VScreenPilot.Perception;

/// <summary>
/// 在 BGRA 缓冲区上画元素框、序号与网格。纯托管实现，避免引入 System.Drawing。
/// 坐标使用屏幕绝对坐标减去捕获原点后传入。
/// </summary>
internal static class PngOverlay
{
    private static readonly (byte R, byte G, byte B)[] Palette =
    {
        (255, 82, 82), (255, 152, 0), (255, 235, 59), (76, 175, 80),
        (0, 188, 212), (33, 150, 243), (156, 39, 176), (233, 30, 99),
    };

    // 3×5 点阵数字，避免依赖字体渲染。
    private static readonly string[] Digits =
    {
        "111101101101111", "010110010010111", "111001111100111", "111001111001111",
        "101101111001001", "111100111001111", "111100111101111", "111001001001001",
        "111101111101111", "111101111001111",
    };

    public static (byte R, byte G, byte B) ColorFor(int index) => Palette[index % Palette.Length];

    public static void DrawGrid(byte[] bgra, int width, int height, int step = 100)
    {
        if (step <= 0)
        {
            return;
        }

        for (int x = step; x < width; x += step)
        {
            for (int y = 0; y < height; y++)
            {
                Blend(bgra, width, height, x, y, 110, 110, 110, 0.35);
            }
        }

        for (int y = step; y < height; y += step)
        {
            for (int x = 0; x < width; x++)
            {
                Blend(bgra, width, height, x, y, 110, 110, 110, 0.35);
            }
        }
    }

    public static void DrawBox(
        byte[] bgra, int width, int height,
        int x, int y, int boxWidth, int boxHeight,
        byte r, byte g, byte b, int thickness = 2)
    {
        if (boxWidth <= 0 || boxHeight <= 0)
        {
            return;
        }

        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = Math.Min(width - 1, x + boxWidth - 1);
        int bottom = Math.Min(height - 1, y + boxHeight - 1);

        if (right < left || bottom < top)
        {
            return;
        }

        for (int t = 0; t < thickness; t++)
        {
            for (int column = left; column <= right; column++)
            {
                Set(bgra, width, height, column, Math.Clamp(top + t, 0, height - 1), r, g, b);
                Set(bgra, width, height, column, Math.Clamp(bottom - t, 0, height - 1), r, g, b);
            }

            for (int row = top; row <= bottom; row++)
            {
                Set(bgra, width, height, Math.Clamp(left + t, 0, width - 1), row, r, g, b);
                Set(bgra, width, height, Math.Clamp(right - t, 0, width - 1), row, r, g, b);
            }
        }
    }

    /// <summary>在 (x,y) 处画一个带底色的小序号，最多两位。</summary>
    public static void DrawNumber(
        byte[] bgra, int width, int height, int x, int y, int value, int scale = 2)
    {
        string digits = Math.Clamp(value, 0, 99).ToString(System.Globalization.CultureInfo.InvariantCulture);
        int glyphWidth = 3 * scale;
        int glyphHeight = 5 * scale;
        int spacing = scale;
        int padding = scale;
        int boxWidth = padding * 2 + digits.Length * glyphWidth + (digits.Length - 1) * spacing;
        int boxHeight = padding * 2 + glyphHeight;
        int left = Math.Clamp(x, 0, Math.Max(0, width - boxWidth));
        int top = Math.Clamp(y, 0, Math.Max(0, height - boxHeight));

        for (int row = 0; row < boxHeight; row++)
        {
            for (int column = 0; column < boxWidth; column++)
            {
                Set(bgra, width, height, left + column, top + row, 245, 245, 245);
            }
        }

        int cursor = left + padding;

        foreach (char digit in digits)
        {
            int index = digit - '0';

            if (index is < 0 or > 9)
            {
                cursor += glyphWidth + spacing;
                continue;
            }

            string glyph = Digits[index];

            for (int gy = 0; gy < 5; gy++)
            {
                for (int gx = 0; gx < 3; gx++)
                {
                    if (glyph[gy * 3 + gx] != '1')
                    {
                        continue;
                    }

                    for (int py = 0; py < scale; py++)
                    {
                        for (int px = 0; px < scale; px++)
                        {
                            Set(bgra, width, height,
                                cursor + gx * scale + px, top + padding + gy * scale + py,
                                20, 20, 20);
                        }
                    }
                }
            }

            cursor += glyphWidth + spacing;
        }
    }

    private static void Set(byte[] bgra, int width, int height, int x, int y, byte r, byte g, byte b)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        int index = (y * width + x) * 4;
        bgra[index] = b;
        bgra[index + 1] = g;
        bgra[index + 2] = r;
        bgra[index + 3] = 255;
    }

    private static void Blend(
        byte[] bgra, int width, int height, int x, int y, byte r, byte g, byte b, double alpha)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
        {
            return;
        }

        int index = (y * width + x) * 4;
        bgra[index] = (byte)(bgra[index] * (1 - alpha) + b * alpha);
        bgra[index + 1] = (byte)(bgra[index + 1] * (1 - alpha) + g * alpha);
        bgra[index + 2] = (byte)(bgra[index + 2] * (1 - alpha) + r * alpha);
        bgra[index + 3] = 255;
    }
}
