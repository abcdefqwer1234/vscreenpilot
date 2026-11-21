using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Mcp;
using static VScreenPilot.Mcp.JsonArgReader;

namespace VScreenPilot.Session;

/// <summary>视觉/断言共用的像素与区域工具，避免在多个组件里各写一份。</summary>
internal static class VisualSupport
{
    public static JsonObject RegionJson(int x, int y, int width, int height) => new()
    {
        ["x"] = x,
        ["y"] = y,
        ["width"] = width,
        ["height"] = height,
    };

    public static (int X, int Y, int Width, int Height) ParseRegion(JsonElement region)
    {
        int x = GetInt(region, "x", int.MinValue);
        int y = GetInt(region, "y", int.MinValue);
        int width = GetInt(region, "width", int.MinValue);
        int height = GetInt(region, "height", int.MinValue);

        if (x == int.MinValue || y == int.MinValue || width <= 0 || height <= 0)
        {
            throw new ToolException("E_INVALID_ARG", "region 需要有效的 x、y、width、height");
        }

        return (x, y, width, height);
    }

    public static void RequireRegionInDisplay(
        DisplayInfo display, int x, int y, int width, int height, string label)
    {
        long right = (long)x + width;
        long bottom = (long)y + height;

        if (width <= 0 || height <= 0 ||
            x < display.X || y < display.Y ||
            right > (long)display.X + display.Width ||
            bottom > (long)display.Y + display.Height)
        {
            throw new ToolException(
                "E_OUT_OF_BOUNDS",
                $"{label} 不在虚拟屏范围内，拒绝读取主屏",
                new JsonObject
                {
                    ["region"] = new JsonObject
                    {
                        ["x"] = x,
                        ["y"] = y,
                        ["width"] = width,
                        ["height"] = height,
                    },
                    ["display"] = new JsonObject
                    {
                        ["x"] = display.X,
                        ["y"] = display.Y,
                        ["width"] = display.Width,
                        ["height"] = display.Height,
                    },
                });
        }
    }

    public static double ChangedRatio(byte[] current, byte[] baseline, int tolerance)
    {
        int count = Math.Min(current.Length, baseline.Length) / 4;
        if (count == 0)
        {
            return 0;
        }

        int changed = 0;

        for (int pixel = 0; pixel < count; pixel++)
        {
            int offset = pixel * 4;
            if (Math.Abs(current[offset] - baseline[offset]) > tolerance ||
                Math.Abs(current[offset + 1] - baseline[offset + 1]) > tolerance ||
                Math.Abs(current[offset + 2] - baseline[offset + 2]) > tolerance)
            {
                changed++;
            }
        }

        return (double)changed / count;
    }

    public static bool ColorMatches(
        byte[] bgra, int offset, byte r, byte g, byte b, int tolerance) =>
        Math.Abs(bgra[offset + 2] - r) <= tolerance &&
        Math.Abs(bgra[offset + 1] - g) <= tolerance &&
        Math.Abs(bgra[offset] - b) <= tolerance;

    public static string ToHex(byte[] bgra, int offset) =>
        $"#{bgra[offset + 2]:X2}{bgra[offset + 1]:X2}{bgra[offset]:X2}";

    public static bool TryParseHex(string value, out byte r, out byte g, out byte b)
    {
        r = g = b = 0;
        string hex = value.Trim().TrimStart('#');

        if (hex.Length == 8)
        {
            hex = hex[2..];
        }

        if (hex.Length != 6 ||
            !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out r) ||
            !byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out g) ||
            !byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out b))
        {
            return false;
        }

        return true;
    }
}
