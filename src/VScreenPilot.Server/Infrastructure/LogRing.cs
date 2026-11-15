using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace VScreenPilot.Infrastructure;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message);

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
    private readonly string? _directory;
    private readonly BlockingCollection<string>? _pending;
    private readonly Thread? _writerThread;

    private string? _filePath;
    private DateTime _fileDate;
    private LogEntry? _lastError;

    public LogRing(int capacity = 500, bool fileEnabled = true)
    {
        _capacity = capacity;

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

    public LogEntry? LastErrorEntry()
    {
        lock (_gate)
        {
            return _lastError;
        }
    }

    public void Add(string level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);

        lock (_gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > _capacity)
            {
                _entries.Dequeue();
            }

            if (string.Equals(level, "ERROR", StringComparison.Ordinal))
            {
                _lastError = entry;
            }
        }

        if (_pending is null)
        {
            return;
        }

        try
        {
            _pending.TryAdd($"{entry.Time:O} [{level}] {message}");
        }
        catch (InvalidOperationException)
        {
            // Dispose 之后仍有日志，丢弃即可。
        }
    }

    private string BuildPath(DateTime date)
    {
        // 多 profile 并行时日志必须能按 profile 归因；default 保持旧文件名。
        string profile = Profile.Resolve();
        string suffix = profile == "default" ? string.Empty : $".{profile}";
        return Path.Combine(_directory!, $"server{suffix}-{date:yyyyMMdd}.log");
    }

    private void CleanupOldLogs()
    {
        if (_directory is null)
        {
            return;
        }

        DateTime cutoff = DateTime.Today.AddDays(-RetentionDays);

        try
        {
            foreach (string file in Directory.EnumerateFiles(_directory, "server*.log*"))
            {
                string name = Path.GetFileName(file);
                string stamp = name.Length >= 14 ? name.Substring(7, 8) : string.Empty;

                if (DateTime.TryParseExact(
                        stamp, "yyyyMMdd", null,
                        System.Globalization.DateTimeStyles.None, out DateTime date) &&
                    date < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 清理失败不影响日志写入。
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
        catch
        {
            // 文件不可用时静默降级为仅内存日志。
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
        catch
        {
            // 忽略。
        }

        try
        {
            stream?.Dispose();
        }
        catch
        {
            // 忽略。
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
        catch
        {
            // 轮转失败时继续写当前文件。
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

    /// <summary>读取日志文件尾部；重启后仍可取证。优先读最新文件。</summary>
    public IReadOnlyList<string> ReadFileTail(int count)
    {
        if (_directory is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            string? newest = Directory.EnumerateFiles(_directory, "server*.log*")
                .Select(path => new FileInfo(path))
                .Where(file => file.Length > 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.Ordinal)
                .Select(file => file.FullName)
                .FirstOrDefault();

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

    public JsonArray TailAsJson(int count)
    {
        var array = new JsonArray();
        foreach (var entry in Tail(count))
        {
            array.Add(ToJson(entry));
        }

        return array;
    }

    public static JsonObject ToJson(LogEntry entry) =>
        new()
        {
            ["time"] = entry.Time.ToString("O"),
            ["level"] = entry.Level,
            ["message"] = entry.Message,
        };

    public static LogEntry? Parse(string line)
    {
        // 格式: 2026-11-09T13:12:19.0000000+08:00 [INFO] message
        int levelStart = line.IndexOf(" [", StringComparison.Ordinal);
        int levelEnd = levelStart < 0 ? -1 : line.IndexOf("] ", levelStart, StringComparison.Ordinal);

        if (levelStart > 0 &&
            levelEnd > levelStart &&
            DateTimeOffset.TryParse(line[..levelStart], out DateTimeOffset time))
        {
            return new LogEntry(
                time,
                line[(levelStart + 2)..levelEnd],
                line[(levelEnd + 2)..]);
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
            catch (ObjectDisposedException)
            {
                // 已释放。
            }
        }

        _writerThread?.Join(1500);
    }
}
