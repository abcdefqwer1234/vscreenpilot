namespace VScreenPilot.Perception;

/// <summary>BGRA 画布。</summary>
internal readonly record struct PngCanvas(byte[] Bgra, int Width, int Height);

/// <summary>画布上的矩形。</summary>
internal readonly record struct PngBox(int X, int Y, int Width, int Height);

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

    public static void DrawGrid(PngCanvas canvas, int step = 100)
    {
        if (step <= 0)
        {
            return;
        }

        for (int x = step; x < canvas.Width; x += step)
        {
            for (int y = 0; y < canvas.Height; y++)
            {
                Blend(canvas, x, y, (110, 110, 110), 0.35);
            }
        }

        for (int y = step; y < canvas.Height; y += step)
        {
            for (int x = 0; x < canvas.Width; x++)
            {
                Blend(canvas, x, y, (110, 110, 110), 0.35);
            }
        }
    }

    public static void DrawBox(
        PngCanvas canvas, PngBox box, (byte R, byte G, byte B) color, int thickness = 2)
    {
        if (box.Width <= 0 || box.Height <= 0)
        {
            return;
        }

        int left = Math.Max(0, box.X);
        int top = Math.Max(0, box.Y);
        int right = Math.Min(canvas.Width - 1, box.X + box.Width - 1);
        int bottom = Math.Min(canvas.Height - 1, box.Y + box.Height - 1);

        if (right < left || bottom < top)
        {
            return;
        }

        for (int t = 0; t < thickness; t++)
        {
            for (int column = left; column <= right; column++)
            {
                Set(canvas, column, Math.Clamp(top + t, 0, canvas.Height - 1), color);
                Set(canvas, column, Math.Clamp(bottom - t, 0, canvas.Height - 1), color);
            }

            for (int row = top; row <= bottom; row++)
            {
                Set(canvas, Math.Clamp(left + t, 0, canvas.Width - 1), row, color);
                Set(canvas, Math.Clamp(right - t, 0, canvas.Width - 1), row, color);
            }
        }
    }

    /// <summary>在 (x,y) 处画一个带底色的小序号，最多两位。</summary>
    public static void DrawNumber(PngCanvas canvas, int x, int y, int value, int scale = 2)
    {
        string digits = Math.Clamp(value, 0, 99).ToString(System.Globalization.CultureInfo.InvariantCulture);
        int glyphWidth = 3 * scale;
        int glyphHeight = 5 * scale;
        int spacing = scale;
        int padding = scale;
        int boxWidth = padding * 2 + digits.Length * glyphWidth + (digits.Length - 1) * spacing;
        int boxHeight = padding * 2 + glyphHeight;
        int left = Math.Clamp(x, 0, Math.Max(0, canvas.Width - boxWidth));
        int top = Math.Clamp(y, 0, Math.Max(0, canvas.Height - boxHeight));

        for (int row = 0; row < boxHeight; row++)
        {
            for (int column = 0; column < boxWidth; column++)
            {
                Set(canvas, left + column, top + row, (245, 245, 245));
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
                            Set(canvas,
                                cursor + gx * scale + px, top + padding + gy * scale + py,
                                (20, 20, 20));
                        }
                    }
                }
            }

            cursor += glyphWidth + spacing;
        }
    }

    private static void Set(PngCanvas canvas, int x, int y, (byte R, byte G, byte B) color)
    {
        if (x < 0 || y < 0 || x >= canvas.Width || y >= canvas.Height)
        {
            return;
        }

        int index = (y * canvas.Width + x) * 4;
        canvas.Bgra[index] = color.B;
        canvas.Bgra[index + 1] = color.G;
        canvas.Bgra[index + 2] = color.R;
        canvas.Bgra[index + 3] = 255;
    }

    private static void Blend(PngCanvas canvas, int x, int y, (byte R, byte G, byte B) color, double alpha)
    {
        if (x < 0 || y < 0 || x >= canvas.Width || y >= canvas.Height)
        {
            return;
        }

        int index = (y * canvas.Width + x) * 4;
        canvas.Bgra[index] = (byte)(canvas.Bgra[index] * (1 - alpha) + color.B * alpha);
        canvas.Bgra[index + 1] = (byte)(canvas.Bgra[index + 1] * (1 - alpha) + color.G * alpha);
        canvas.Bgra[index + 2] = (byte)(canvas.Bgra[index + 2] * (1 - alpha) + color.R * alpha);
        canvas.Bgra[index + 3] = 255;
    }
}
