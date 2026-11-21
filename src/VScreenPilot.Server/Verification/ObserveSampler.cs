using VScreenPilot.Views;
using VScreenPilot.Platform.Displays;

namespace VScreenPilot.Verification;

/// <summary>
/// 动作后置验证：动作前后各取一次 16×16 网格签名，判断画面是否真的变化。
/// 只依赖一个“区域截图”委托，便于单测替换。
/// </summary>
internal sealed class ObserveSampler
{
    private const double ChangeThreshold = 0.005;
    private const int Grid = 16;
    private const int ChannelTolerance = 12;

    private readonly Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> _capture;

    public ObserveSampler(Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> capture)
    {
        _capture = capture;
    }

    public sealed record Signature(
        byte[] Cells, int Grid, string Hash, int X, int Y, int Width, int Height);

    public Signature Capture(DisplayInfo display, int x, int y, int width, int height)
    {
        int left = Math.Clamp(x, display.X, display.X + display.Width - 1);
        int top = Math.Clamp(y, display.Y, display.Y + display.Height - 1);
        int right = Math.Clamp(x + width, left + 1, display.X + display.Width);
        int bottom = Math.Clamp(y + height, top + 1, display.Y + display.Height);
        int captureWidth = Math.Max(1, right - left);
        int captureHeight = Math.Max(1, bottom - top);

        (byte[] bgra, int sourceWidth, int sourceHeight) =
            _capture(left, top, captureWidth, captureHeight);

        byte[] cells = new byte[Grid * Grid * 3];

        for (int gy = 0; gy < Grid; gy++)
        {
            int sy0 = gy * sourceHeight / Grid;
            int sy1 = Math.Max(sy0 + 1, (gy + 1) * sourceHeight / Grid);

            for (int gx = 0; gx < Grid; gx++)
            {
                int sx0 = gx * sourceWidth / Grid;
                int sx1 = Math.Max(sx0 + 1, (gx + 1) * sourceWidth / Grid);
                long blue = 0, green = 0, red = 0;
                int count = 0;

                for (int sy = sy0; sy < sy1 && sy < sourceHeight; sy++)
                {
                    int row = sy * sourceWidth * 4;

                    for (int sx = sx0; sx < sx1 && sx < sourceWidth; sx++)
                    {
                        int index = row + sx * 4;
                        blue += bgra[index];
                        green += bgra[index + 1];
                        red += bgra[index + 2];
                        count++;
                    }
                }

                int cell = (gy * Grid + gx) * 3;
                count = Math.Max(1, count);
                cells[cell] = (byte)(red / count);
                cells[cell + 1] = (byte)(green / count);
                cells[cell + 2] = (byte)(blue / count);
            }
        }

        return new Signature(
            cells, Grid, Hash(cells), left, top, captureWidth, captureHeight);
    }

    public static ObservedView? Build(Signature? before, Signature? after, double milliseconds)
    {
        if (before is null || after is null)
        {
            return null;
        }

        int changed = 0;

        for (int cell = 0; cell < before.Cells.Length; cell += 3)
        {
            int delta = Math.Max(
                Math.Abs(before.Cells[cell] - after.Cells[cell]),
                Math.Max(
                    Math.Abs(before.Cells[cell + 1] - after.Cells[cell + 1]),
                    Math.Abs(before.Cells[cell + 2] - after.Cells[cell + 2])));

            if (delta > ChannelTolerance)
            {
                changed++;
            }
        }

        int total = before.Grid * before.Grid;
        double ratio = Math.Round((double)changed / Math.Max(1, total), 4);

        return new ObservedView(
            ratio >= ChangeThreshold,
            ratio,
            ChangeThreshold,
            before.Grid,
            before.Hash,
            after.Hash,
            Math.Round(milliseconds, 1),
            new RegionView(before.X, before.Y, before.Width, before.Height));
    }

    private static string Hash(byte[] cells)
    {
        ulong hash = 14695981039346656037UL;

        foreach (byte value in cells)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}
