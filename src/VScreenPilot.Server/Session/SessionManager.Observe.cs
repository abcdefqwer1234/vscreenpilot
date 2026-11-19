using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;

namespace VScreenPilot.Session;

/// <summary>
/// 动作后置验证：动作前后各取一次缩略图签名，返回是否真的发生了变化。
/// 只做 16×16 网格采样，成本约等于一次区域截图。
/// </summary>
internal sealed partial class SessionManager
{
    internal sealed record ObserveSignature(
        byte[] Cells, int Grid, string Hash, int X, int Y, int Width, int Height);

    private const double ObserveChangeThreshold = 0.005;
    private const int ObserveGrid = 16;
    private const int ObserveChannelTolerance = 12;

    private ObserveSignature CaptureSignature(
        DisplayInfo display, int x, int y, int width, int height)
    {
        int left = Math.Clamp(x, display.X, display.X + display.Width - 1);
        int top = Math.Clamp(y, display.Y, display.Y + display.Height - 1);
        int right = Math.Clamp(x + width, left + 1, display.X + display.Width);
        int bottom = Math.Clamp(y + height, top + 1, display.Y + display.Height);
        int captureWidth = Math.Max(1, right - left);
        int captureHeight = Math.Max(1, bottom - top);

        (byte[] bgra, int sourceWidth, int sourceHeight) =
            CaptureRegion(left, top, captureWidth, captureHeight);

        byte[] cells = new byte[ObserveGrid * ObserveGrid * 3];

        for (int gy = 0; gy < ObserveGrid; gy++)
        {
            int sy0 = gy * sourceHeight / ObserveGrid;
            int sy1 = Math.Max(sy0 + 1, (gy + 1) * sourceHeight / ObserveGrid);

            for (int gx = 0; gx < ObserveGrid; gx++)
            {
                int sx0 = gx * sourceWidth / ObserveGrid;
                int sx1 = Math.Max(sx0 + 1, (gx + 1) * sourceWidth / ObserveGrid);
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

                int cell = (gy * ObserveGrid + gx) * 3;
                count = Math.Max(1, count);
                cells[cell] = (byte)(red / count);
                cells[cell + 1] = (byte)(green / count);
                cells[cell + 2] = (byte)(blue / count);
            }
        }

        return new ObserveSignature(
            cells, ObserveGrid, SignatureHash(cells), left, top, captureWidth, captureHeight);
    }

    private static JsonObject? BuildObserved(
        ObserveSignature? before, ObserveSignature? after, double milliseconds)
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

            if (delta > ObserveChannelTolerance)
            {
                changed++;
            }
        }

        int total = before.Grid * before.Grid;
        double ratio = Math.Round((double)changed / Math.Max(1, total), 4);

        return new JsonObject
        {
            ["changed"] = ratio >= ObserveChangeThreshold,
            ["ratio"] = ratio,
            ["threshold"] = ObserveChangeThreshold,
            ["grid"] = before.Grid,
            ["before"] = before.Hash,
            ["after"] = after.Hash,
            ["ms"] = Math.Round(milliseconds, 1),
            ["region"] = new JsonObject
            {
                ["x"] = before.X,
                ["y"] = before.Y,
                ["width"] = before.Width,
                ["height"] = before.Height,
            },
        };
    }

    private static string SignatureHash(byte[] cells)
    {
        ulong hash = 14695981039346656037UL;

        foreach (byte value in cells)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }

        return hash.ToString("x16");
    }
}
