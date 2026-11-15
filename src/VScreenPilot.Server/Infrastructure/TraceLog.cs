using System.Text;
using System.Text.Json.Nodes;

namespace VScreenPilot.Infrastructure;

/// <summary>
/// 每会话一份 JSONL 追踪文件。记录每次工具调用的耗时、错误码、返回字节与通道，
/// 让失败可以被复盘（报告里不再只有“E_INTERNAL”）。
/// </summary>
internal sealed class TraceLog : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter? _writer;
    private int _disposed;

    public string FilePath { get; } = string.Empty;

    public TraceLog()
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot",
                "sessions");
            Directory.CreateDirectory(root);
            CleanupOldSessions(root);

            string profile = Profile.Resolve();
            string profilePart = profile == "default" ? string.Empty : $"{profile}-";
            FilePath = Path.Combine(
                root,
                $"session-{profilePart}{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.jsonl");
            _writer = new StreamWriter(
                new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            {
                AutoFlush = true,
            };
        }
        catch
        {
            // 追踪失败不能影响主流程（例如只读用户目录）。
            _writer = null;
        }
    }

    /// <summary>会话追踪不无限增长：删除 7 天前的文件，并最多保留 50 份。</summary>
    private static void CleanupOldSessions(string root)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            List<FileInfo> files = new DirectoryInfo(root)
                .EnumerateFiles("session-*.jsonl")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            for (int index = 0; index < files.Count; index++)
            {
                if (index >= 50 || files[index].LastWriteTimeUtc < cutoff)
                {
                    try
                    {
                        files[index].Delete();
                    }
                    catch
                    {
                        // 正在写入的文件删不掉，下一轮再试。
                    }
                }
            }
        }
        catch
        {
            // 清理失败不影响追踪。
        }
    }

    public void Write(string type, JsonObject? payload = null)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var record = payload?.DeepClone() as JsonObject ?? new JsonObject();
        record["ts"] = DateTimeOffset.UtcNow.ToString("O");
        record["type"] = type;

        string line = record.ToJsonString();

        lock (_lock)
        {
            try
            {
                _writer?.WriteLine(line);
            }
            catch
            {
                // 忽略磁盘写入失败。
            }
        }
    }

    public void ToolCall(
        string tool,
        string requestId,
        string? arguments,
        double milliseconds,
        string? code,
        bool isError,
        int textBytes,
        int imageBytes,
        string? channel,
        string? stage)
    {
        Write("tool_call", new JsonObject
        {
            ["request_id"] = requestId,
            ["tool"] = tool,
            ["args"] = arguments,
            ["ms"] = Math.Round(milliseconds, 2),
            ["code"] = code,
            ["is_error"] = isError,
            ["text_bytes"] = textBytes,
            ["image_bytes"] = imageBytes,
            ["channel"] = channel,
            ["stage"] = stage,
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }
    }
}
