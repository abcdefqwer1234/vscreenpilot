using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Server.Infrastructure;

public sealed record LogEntry(
    DateTimeOffset Time, string Level, string Message, string? ClientId = null);

/// <summary>内存环形日志 + 按天/按大小轮转的文件日志（文件用于崩溃后取证）。</summary>
public sealed class LogRing : IDisposable
{
    internal const long MaxFileBytes = 8L * 1024 * 1024;
    internal const int MaxBackups = 3;
    internal const int RetentionDays = 14;
    internal const int FileTailBytes = 512 * 1024;

    internal readonly object _gate = new();
    internal readonly Queue<LogEntry> _entries = new();
    internal readonly int _capacity;
    internal readonly bool _clientLog;
    internal readonly string? _directory;
    internal readonly BlockingCollection<string>? _pending;
    internal readonly Thread? _writerThread;

    internal string? _filePath;
    internal DateTime _fileDate;

    /// <summary>由 SessionManager 注入；用于把日志归到当前 MCP 客户端。</summary>
    /// <summary>由会话注入：把日志归到当前 MCP 客户端。实例级，避免静态服务定位器。</summary>
    public Func<string?>? ClientIdProvider { get; set; }

    internal readonly LogFileStore FileStore;

    public LogRing(int capacity = 500, bool fileEnabled = true, bool clientLog = false)
    {
        FileStore = new LogFileStore(this);
        _capacity = capacity;
        _clientLog = clientLog;

        if (!fileEnabled)
        {
            return;
        }

        try
        {
            _directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot", "logs");
            Directory.CreateDirectory(_directory);
            FileStore.CleanupOldLogs();
            _fileDate = DateTime.Today;
            _filePath = FileStore.BuildPath(_fileDate);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            _directory = null;
            _filePath = null;
        }

        if (_filePath is null)
        {
            return;
        }

        // 文件写入放到后台线程，避免每条日志都同步开关文件拖慢工具调用。
        _pending = new BlockingCollection<string>(new ConcurrentQueue<string>());
        _writerThread = new Thread(FileStore.WriteLoop)
        {
            IsBackground = true,
            Name = "LogWriter",
        };
        _writerThread.Start();
    }

    public string? FilePath => _filePath;

    public void Info(string message) => Add("INFO", message);

    public void Warn(string message) => Add("WARN", message);

    public void Error(string message, Exception? exception = null) =>
        Add("ERROR", exception is null
            ? message
            : $"{message}: {exception.GetType().Name}: {exception.Message}");

    public void Add(string level, string message)
    {
        string? clientId = null;

        try
        {
            clientId = ClientIdProvider?.Invoke();
        }
        catch (Exception exception)
        {
            // 归属解析失败不影响日志。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        var entry = new LogEntry(DateTimeOffset.Now, level, message, clientId);

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }
        }

        if (_pending is null)
        {
            return;
        }

        try
        {
            string clientPart = string.IsNullOrWhiteSpace(clientId) ? string.Empty : $" [client={clientId}]";
            _pending.TryAdd($"{entry.Time:O} [{level}]{clientPart} {message}");
        }
        catch (InvalidOperationException exception)
        {
            // Dispose 之后仍有日志，丢弃即可。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }

    public IReadOnlyList<LogEntry> Tail(int count)
    {
        lock (_gate)
        {
            int skip = Math.Max(0, _entries.Count - Math.Clamp(count, 1, _capacity));
            return _entries.Skip(skip).ToArray();
        }
    }

    /// <summary>读取日志文件尾部；重启后仍可取证。优先读本进程文件，其次同 profile 的最新文件。</summary>
    public static JsonObject ToJson(LogEntry entry)
    {
        var json = new JsonObject
        {
            ["time"] = entry.Time.ToString("O"),
            ["level"] = entry.Level,
            ["message"] = entry.Message,
        };

        if (!string.IsNullOrWhiteSpace(entry.ClientId))
        {
            json["client_id"] = entry.ClientId;
        }

        return json;
    }

    public static LogEntry? Parse(string line)
    {
        // 格式: 2026-11-09T13:12:19.0000000+08:00 [INFO] [client=c1] message
        // 旧文件没有 [client=...]，按“服务端日志”处理。
        int levelStart = line.IndexOf(" [", StringComparison.Ordinal);
        int levelEnd = levelStart < 0 ? -1 : line.IndexOf("] ", levelStart, StringComparison.Ordinal);

        if (levelStart > 0 &&
            levelEnd > levelStart &&
            DateTimeOffset.TryParse(line[..levelStart], out DateTimeOffset time))
        {
            string message = line[(levelEnd + 2)..];
            string? clientId = null;

            if (message.StartsWith("[client=", StringComparison.Ordinal))
            {
                int clientEnd = message.IndexOf("] ", StringComparison.Ordinal);

                if (clientEnd > "[client=".Length)
                {
                    clientId = message["[client=".Length..clientEnd];
                    message = message[(clientEnd + 2)..];
                }
            }

            return new LogEntry(time, line[(levelStart + 2)..levelEnd], message, clientId);
        }

        return null;
    }

    public void Dispose()
    {
        if (_pending is not null)
        {
            try
            {
                _pending.CompleteAdding();
            }
            catch (ObjectDisposedException exception)
            {
                // 已释放。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }

        _writerThread?.Join(1500);
    }

    internal void CleanupOldLogs() => FileStore.CleanupOldLogs();

    public IReadOnlyList<string> ReadFileTail(int count) => FileStore.ReadFileTail(count);
}
