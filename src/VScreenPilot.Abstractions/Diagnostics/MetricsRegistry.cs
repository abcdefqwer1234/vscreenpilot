using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using VScreenPilot.Views;

namespace VScreenPilot.Infrastructure;

/// <summary>
/// 进程内轻量指标。没有外部 metrics 依赖，随 get_environment 一起返回。
/// 计数用 Interlocked，按工具/错误码的字典用 ConcurrentDictionary。
/// </summary>
internal static class MetricsRegistry
{
    private static long _calls;
    private static long _errors;
    private static long _totalMs;
    private static long _maxMs;
    private static long _textBytes;
    private static long _imageBytes;
    private static long _snapshotNodes;
    private static long _ocrCalls;

    private static readonly ConcurrentDictionary<string, long> ByTool = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, long> ByCode = new(StringComparer.Ordinal);

    public static void RecordTool(
        string tool, double milliseconds, string? code, bool isError, int text, int image)
    {
        Interlocked.Increment(ref _calls);
        Interlocked.Add(ref _totalMs, (long)Math.Round(milliseconds));
        InterlockedMax(ref _maxMs, (long)Math.Round(milliseconds));
        Interlocked.Add(ref _textBytes, text);
        Interlocked.Add(ref _imageBytes, image);

        if (isError)
        {
            Interlocked.Increment(ref _errors);
        }

        if (!string.IsNullOrEmpty(code))
        {
            ByCode.AddOrUpdate(code, 1, (_, value) => value + 1);
        }

        ByTool.AddOrUpdate(tool, 1, (_, value) => value + 1);
    }

    public static void RecordSnapshot(int nodes) =>
        Interlocked.Add(ref _snapshotNodes, Math.Max(0, nodes));

    public static void RecordOcr() => Interlocked.Increment(ref _ocrCalls);

    public static JsonObject ToJson()
    {
        long calls = Interlocked.Read(ref _calls);
        long totalMs = Interlocked.Read(ref _totalMs);

        var tools = new JsonObject();

        foreach ((string name, long count) in ByTool.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            tools[name] = count;
        }

        var codes = new JsonObject();

        foreach ((string name, long count) in ByCode.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            codes[name] = count;
        }

        return new JsonObject
        {
            ["calls"] = calls,
            ["errors"] = Interlocked.Read(ref _errors),
            ["error_rate"] = calls == 0 ? 0 : Math.Round((double)Interlocked.Read(ref _errors) / calls, 4),
            ["avg_ms"] = calls == 0 ? 0 : Math.Round((double)totalMs / calls, 1),
            ["max_ms"] = Interlocked.Read(ref _maxMs),
            ["text_bytes"] = Interlocked.Read(ref _textBytes),
            ["image_bytes"] = Interlocked.Read(ref _imageBytes),
            ["snapshot_nodes"] = Interlocked.Read(ref _snapshotNodes),
            ["ocr_calls"] = Interlocked.Read(ref _ocrCalls),
            ["by_tool"] = tools,
            ["by_code"] = codes,
        };
    }

    /// <summary>快照为强类型视图；by_tool/by_code 按 Ordinal 排序，保证输出稳定。</summary>
    public static MetricsView ToView()
    {
        long calls = Interlocked.Read(ref _calls);
        long totalMs = Interlocked.Read(ref _totalMs);
        long errors = Interlocked.Read(ref _errors);

        var tools = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach ((string name, long count) in ByTool)
        {
            tools[name] = count;
        }

        var codes = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach ((string name, long count) in ByCode)
        {
            codes[name] = count;
        }

        return new MetricsView
        {
            Calls = calls,
            Errors = errors,
            ErrorRate = calls == 0 ? 0 : Math.Round((double)errors / calls, 4),
            AvgMs = calls == 0 ? 0 : Math.Round((double)totalMs / calls, 1),
            MaxMs = Interlocked.Read(ref _maxMs),
            TextBytes = Interlocked.Read(ref _textBytes),
            ImageBytes = Interlocked.Read(ref _imageBytes),
            SnapshotNodes = Interlocked.Read(ref _snapshotNodes),
            OcrCalls = Interlocked.Read(ref _ocrCalls),
            ByTool = tools,
            ByCode = codes,
        };
    }

    private static void InterlockedMax(ref long target, long value)    {
        long current = Interlocked.Read(ref target);

        while (value > current)
        {
            long previous = Interlocked.CompareExchange(ref target, value, current);

            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }
}
