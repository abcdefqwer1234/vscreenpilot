namespace VScreenPilot.Perception;

/// <summary>
/// 位图放大：默认 bilinear，VSP_OCR_UPSCALE=nearest 可切最近邻。
/// 只用于 OCR 前的预处理，不改变坐标映射。
/// </summary>
internal static class BitmapOps
{
    private static readonly bool UseNearest = string.Equals(
        Environment.GetEnvironmentVariable("VSP_OCR_UPSCALE"),
        "nearest",
        StringComparison.OrdinalIgnoreCase);

    public static (byte[] Bgra, int Width, int Height) Upscale(
        byte[] bgra, int width, int height, int factor)
    {
        if (factor <= 1 || width <= 0 || height <= 0)
        {
            return (bgra, width, height);
        }

        int outputWidth = width * factor;
        int outputHeight = height * factor;
        var output = new byte[outputWidth * outputHeight * 4];

        if (UseNearest)
        {
            UpscaleNearest(bgra, width, height, factor, output, outputWidth, outputHeight);
        }
        else
        {
            UpscaleBilinear(bgra, width, height, factor, output, outputWidth, outputHeight);
        }

        return (output, outputWidth, outputHeight);
    }

    private static void UpscaleNearest(
        byte[] bgra, int width, int height, int factor,
        byte[] output, int outputWidth, int outputHeight)
    {
        for (int y = 0; y < outputHeight; y++)
        {
            int sourceY = y / factor;
            int sourceRow = sourceY * width * 4;
            int outputRow = y * outputWidth * 4;

            for (int x = 0; x < outputWidth; x++)
            {
                int sourceIndex = sourceRow + (x / factor) * 4;
                int outputIndex = outputRow + x * 4;
                output[outputIndex] = bgra[sourceIndex];
                output[outputIndex + 1] = bgra[sourceIndex + 1];
                output[outputIndex + 2] = bgra[sourceIndex + 2];
                output[outputIndex + 3] = bgra[sourceIndex + 3];
            }
        }
    }

    private static void UpscaleBilinear(
        byte[] bgra, int width, int height, int factor,
        byte[] output, int outputWidth, int outputHeight)
    {
        for (int y = 0; y < outputHeight; y++)
        {
            double sourceY = ((y + 0.5) / factor) - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(sourceY), 0, height - 1);
            int y1 = Math.Min(y0 + 1, height - 1);
            double wy = Math.Clamp(sourceY - y0, 0, 1);
            int outputRow = y * outputWidth * 4;

            for (int x = 0; x < outputWidth; x++)
            {
                double sourceX = ((x + 0.5) / factor) - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(sourceX), 0, width - 1);
                int x1 = Math.Min(x0 + 1, width - 1);
                double wx = Math.Clamp(sourceX - x0, 0, 1);
                int i00 = (y0 * width + x0) * 4;
                int i10 = (y0 * width + x1) * 4;
                int i01 = (y1 * width + x0) * 4;
                int i11 = (y1 * width + x1) * 4;
                int outputIndex = outputRow + x * 4;

                for (int channel = 0; channel < 4; channel++)
                {
                    double top = bgra[i00 + channel] * (1 - wx) + bgra[i10 + channel] * wx;
                    double bottom = bgra[i01 + channel] * (1 - wx) + bgra[i11 + channel] * wx;
                    output[outputIndex + channel] =
                        (byte)Math.Clamp((int)Math.Round(top * (1 - wy) + bottom * wy), 0, 255);
                }
            }
        }
    }
}
