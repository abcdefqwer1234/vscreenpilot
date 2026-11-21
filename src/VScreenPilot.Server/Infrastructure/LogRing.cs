using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace VScreenPilot.Infrastructure;

public sealed record LogEntry(
    DateTimeOffset Time, string Level, string Message, string? ClientId = null);

/// <summary>内存环形日志 + 按天/按大小轮转的文件日志（文件用于崩溃后取证）。</summary>
public sealed class LogRing : IDisposable
{
    private const long MaxFileBytes = 8L * 1024 * 1024;
    private const int MaxBackups = 3;
    private const int RetentionDays = 14;
    private const int FileTailBytes = 512 * 1024;

    private readonly object _gate = new();
    private readonly Queue<LogEntry> _entries = new();
    private readonly int _capacity;
    private readonly bool _clientLog;
    private readonly string? _directory;
    private readonly BlockingCollection<string>? _pending;
    private readonly Thread? _writerThread;

    private string? _filePath;
    private DateTime _fileDate;

    /// <summary>由 SessionManager 注入；用于把日志归到当前 MCP 客户端。</summary>
    /// <summary>由会话注入：把日志归到当前 MCP 客户端。实例级，避免静态服务定位器。</summary>
    public Func<string?>? ClientIdProvider { get; set; }

    public LogRing(int capacity = 500, bool fileEnabled = true, bool clientLog = false)
    {
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
            CleanupOldLogs();
            _fileDate = DateTime.Today;
            _filePath = BuildPath(_fileDate);
        }
        catch
        {
            _directory = null;
            _filePath = null;
        }

        if (_filePath is null)
        {
            return;
        }

        // 文件写入放到后台线程，避免每条日志都同步开关文件拖慢工具调用。
        _pending = new BlockingCollection<string>(new ConcurrentQueue<string>());
        _writerThread = new Thread(WriteLoop)
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
        Add("ERROR", exception is null ? message : $"{message}: {exception.GetType().Name}: {exception.Message}");

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
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
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
        catch (InvalidOperationException exception){
            // Dispose 之后仍有日志，丢弃即可。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }
    }

    private string BuildPath(DateTime date)
    {
        // profile + pid：共享实例里 daemon 和 proxy 是不同进程，不能写同一个文件；
        // proxy 用 client-*，避免 daemon 的 get_log(source=file) 在回退时读到它。
        if (_clientLog)
        {
            return Path.Combine(
                _directory!,
                $"client-{Environment.ProcessId}-{date:yyyyMMdd}.log");
        }

        string profile = Profile.Resolve();
        string suffix = profile == "default" ? string.Empty : $".{profile}";
        return Path.Combine(
            _directory!,
            $"server{suffix}-{Environment.ProcessId}-{date:yyyyMMdd}.log");
    }

    internal void CleanupOldLogs()
    {
        if (_directory is null)
        {
            return;
        }

        DateTime cutoff = DateTime.Today.AddDays(-RetentionDays);

        try
        {
            IEnumerable<string> files = Directory.EnumerateFiles(_directory, "server*.log*")
                .Concat(Directory.EnumerateFiles(_directory, "client*.log*"));

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);

                // 文件名可能是：
                //   server-20261118.log                  （旧默认 profile）
                //   server.<profile>-20261118.log         （旧 profile）
                //   server-<pid>-20261118.log             （新默认 profile）
                //   server.<profile>-<pid>-20261118.log   （新 profile）
                // 统一取 ".log" 前最后一个 '-' 后面的 8 位日期。
                int logIndex = name.IndexOf(".log", StringComparison.OrdinalIgnoreCase);

                if (logIndex < 0)
                {
                    continue;
                }

                string stem = name[..logIndex];
                int dash = stem.LastIndexOf('-');
                string stamp = dash >= 0 && stem.Length - dash - 1 == 8
                    ? stem[(dash + 1)..]
                    : string.Empty;

                if (DateTime.TryParseExact(
                        stamp, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out DateTime date) &&
                    date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception exception)
        {
            // 清理失败不影响日志写入。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }
    }

    private void WriteLoop()
    {
        if (_filePath is null || _pending is null)
        {
            return;
        }

        FileStream? stream = null;
        StreamWriter? writer = null;

        try
        {
            OpenWriter(ref stream, ref writer);

            foreach (string line in _pending.GetConsumingEnumerable())
            {
                try
                {
                    if (DateTime.Today != _fileDate)
                    {
                        _fileDate = DateTime.Today;
                        _filePath = BuildPath(_fileDate);
                        CloseWriter(ref stream, ref writer);
                        CleanupOldLogs();
                        OpenWriter(ref stream, ref writer);
                    }

                    if (writer is null || stream is null)
                    {
                        OpenWriter(ref stream, ref writer);
                    }

                    writer!.WriteLine(line);

                    if (stream!.Length >= MaxFileBytes)
                    {
                        CloseWriter(ref stream, ref writer);
                        RotateFiles();
                        OpenWriter(ref stream, ref writer);
                    }
                }
                catch
                {
                    // 日志失败不能影响主流程；尝试重新打开文件。
                    CloseWriter(ref stream, ref writer);
                }
            }
        }
        catch (Exception exception)
        {
            // 文件不可用时静默降级为仅内存日志。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }
        finally
        {
            CloseWriter(ref stream, ref writer);
        }
    }

    private void OpenWriter(ref FileStream? stream, ref StreamWriter? writer)
    {
        if (_filePath is null)
        {
            return;
        }

        stream = new FileStream(
            _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
    }

    private static void CloseWriter(ref FileStream? stream, ref StreamWriter? writer)
    {
        try
        {
            writer?.Dispose();
        }
        catch (Exception exception)
        {
            // 忽略。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }

        try
        {
            stream?.Dispose();
        }
        catch (Exception exception)
        {
            // 忽略。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }

        writer = null;
        stream = null;
    }

    private void RotateFiles()
    {
        if (_filePath is null)
        {
            return;
        }

        try
        {
            for (int index = MaxBackups; index >= 1; index--)
            {
                string source = index == 1 ? _filePath : $"{_filePath}.{index - 1}";
                string target = $"{_filePath}.{index}";

                if (File.Exists(source))
                {
                    File.Move(source, target, overwrite: true);
                }
            }
        }
        catch (Exception exception)
        {
            // 轮转失败时继续写当前文件。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
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
    public IReadOnlyList<string> ReadFileTail(int count)
    {
        if (_directory is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            string? newest = null;

            if (_filePath is not null && File.Exists(_filePath) && new FileInfo(_filePath).Length > 0)
            {
                newest = _filePath;
            }
            else
            {
                // 按角色 + profile 过滤，避免 get_log(source=file) 读到 proxy/其他 profile 的日志。
                string profile = Profile.Resolve();
                string prefix = _clientLog
                    ? "client-"
                    : profile == "default" ? "server-" : $"server.{profile}-";
                newest = Directory.EnumerateFiles(_directory, prefix + "*.log*")
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Length > 0)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                    .Select(file => file.FullName)
                    .FirstOrDefault();
            }

            if (newest is null)
            {
                return Array.Empty<string>();
            }

            using var stream = new FileStream(
                newest, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, stream.Length - FileTailBytes);
            stream.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            string text = reader.ReadToEnd();
            var lines = text
                .Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .ToList();

            if (start > 0 && lines.Count > 0)
            {
                // 第一条可能是被截断的半行。
                lines.RemoveAt(0);
            }

            return lines.TakeLast(Math.Clamp(count, 1, 500)).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

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
            catch (ObjectDisposedException exception){
                // 已释放。
                System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
            }
        }

        _writerThread?.Join(1500);
    }
}
