using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Perception;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Views;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

/// <summary>ComponentsOps：VisualMatchService 的匹配子集。</summary>
internal sealed class VisualComponentsOps
{
    private readonly VisualMatchService _core;

    public VisualComponentsOps(VisualMatchService core) => _core = core;
    internal byte[] DecodeBase64Png(string value)
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

        if (data.Length > VisualMatchService.MaxTemplateBytes)
        {
            throw new ToolException(
                "E_UNSUPPORTED_IMAGE",
                $"模板解码后过大: {data.Length} 字节（上限 {VisualMatchService.MaxTemplateBytes}）",
                new JsonObject { ["bytes"] = data.Length });
        }

        return data;
    }

    internal readonly record struct ColorComponent(
        int Left, int Top, int Right, int Bottom, int Pixels);

    /// <summary>4 连通域标记：返回连通区域的边界与像素数。</summary>
    internal List<ColorComponent> ConnectedComponents(
        byte[] mask, int width, int height, int minPixels, Func<bool> isCancelled)
    {
        var components = new List<ColorComponent>();
        int total = width * height;
        var visited = ArrayPool<byte>.Shared.Rent(total);
        var queue = ArrayPool<int>.Shared.Rent(total);

        try
        {
            Array.Clear(visited, 0, total);
            var scanner = new ColorComponentScanner(mask, width, height, queue);

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

                ColorComponent component = scanner.Fill(start, visited);

                // 小于阈值的碎片直接丢弃，避免碎片化界面产生海量无用连通域。
                if (component.Pixels >= minPixels)
                {
                    components.Add(component);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(visited);
            ArrayPool<int>.Shared.Return(queue);
        }

        return components;
    }
}
