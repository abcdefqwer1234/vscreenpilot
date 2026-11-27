using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>工具调用的请求侧字段。</summary>
internal sealed record TraceRequest(
    string Tool, string RequestId, string? Arguments, double Milliseconds, string? ClientId);

/// <summary>工具调用的响应侧字段。</summary>
internal sealed record TraceResult(
    string? Code, bool IsError, int TextBytes, int ImageBytes, string? Channel, string? Stage,
    string? Summary = null);

/// <summary>
/// 每会话一份 JSONL 追踪文件。记录每次工具调用的耗时、错误码、返回字节与通道，
/// 让失败可以被复盘（报告里不再只有“E_INTERNAL”）。
/// </summary>
internal sealed class TraceLog : IDisposable
{
    private readonly object _lock = new();
    private readonly StreamWriter? _writer;
    private int _disposed;
    private int _pending;

    public string FilePath { get; } = string.Empty;

    public TraceLog()
    {
        try
        {
            string root = SessionRoot();
            Directory.CreateDirectory(root);
            CleanupOldSessions(root);

            string profile = Profile.Resolve();
            string profilePart = profile == "default" ? string.Empty : $"{profile}-";
            string nonce = Guid.NewGuid().ToString("N")[..6];
            FilePath = Path.Combine(
                root,
                $"session-{profilePart}{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{nonce}.jsonl");
            _writer = new StreamWriter(
                new FileStream(FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read),
                new UTF8Encoding(false))
            {
                AutoFlush = false,
            };
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            // 追踪失败不能影响主流程（例如只读用户目录）。
            _writer = null;
        }
    }

    private static string SessionRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VScreenPilot",
        "sessions");

    /// <summary>daemon 周期维护入口；目录不存在时静默跳过。</summary>
    internal static void CleanupSessions()
    {
        string root = SessionRoot();

        if (Directory.Exists(root))
        {
            CleanupOldSessions(root);
        }
    }

    /// <summary>会话追踪不无限增长：删除 7 天前的文件，并最多保留 50 份（不动最近 1 小时内仍在写的）。</summary>
    private static void CleanupOldSessions(string root)
    {
        try
        {
            DateTime cutoff = DateTime.UtcNow.AddDays(-7);
            DateTime activeCutoff = DateTime.UtcNow.AddHours(-1);
            List<FileInfo> files = new DirectoryInfo(root)
                .EnumerateFiles("session-*.jsonl")
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            for (int index = 0; index < files.Count; index++)
            {
                bool expired = files[index].LastWriteTimeUtc < cutoff;
                bool overLimit = index >= 50 && files[index].LastWriteTimeUtc < activeCutoff;

                if (!expired && !overLimit)
                {
                    continue;
                }

                try
                {
                    files[index].Delete();
                }
                catch (Exception exception)
                {
                    // 正在写入的文件删不掉，下一轮再试。
                    LogSink.Debug($"忽略异常: {exception.Message}");
                }
            }
        }
        catch (Exception exception)
        {
            // 清理失败不影响追踪。
            LogSink.Debug($"忽略异常: {exception.Message}");
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

                // 工具调用是低频事件：每 16 条刷一次，崩溃最多丢 15 条；
                // 比每条都 flush 更省，也避免 daemon 共享文件上的锁竞争放大到磁盘 IO。
                if (++_pending >= 16)
                {
                    _writer?.Flush();
                    _pending = 0;
                }
            }
            catch (Exception exception)
            {
                // 忽略磁盘写入失败。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }
    }

    /// <summary>把当前缓冲强制落盘；错误记录和退出路径使用。</summary>
    public void Flush()
    {
        lock (_lock)
        {
            try
            {
                _writer?.Flush();
                _pending = 0;
            }
            catch (Exception exception)
            {
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }
    }

    public void ToolCall(TraceRequest request, TraceResult result)
    {
        Write("tool_call", new JsonObject
        {
            ["request_id"] = request.RequestId,
            ["client_id"] = request.ClientId,
            ["tool"] = request.Tool,
            ["args"] = request.Arguments,
            ["ms"] = Math.Round(request.Milliseconds, 2),
            ["code"] = result.Code,
            ["is_error"] = result.IsError,
            ["text_bytes"] = result.TextBytes,
            ["image_bytes"] = result.ImageBytes,
            ["channel"] = result.Channel,
            ["stage"] = result.Stage,
            ["summary"] = result.Summary,
        });

        // 失败记录不参与批量缓冲：复盘时最需要的正是这些。
        if (result.IsError)
        {
            Flush();
        }
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
            catch (Exception exception)
            {
                // 忽略。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }
    }
}
