using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>日志文件落盘、轮转与读取；从 LogRing 拆出。</summary>
internal sealed class LogFileStore
{
    private readonly LogRing _host;

    public LogFileStore(LogRing host) => _host = host;

    internal string BuildPath(DateTime date)
    {
        // profile + pid：共享实例里 daemon 和 proxy 是不同进程，不能写同一个文件；
        // proxy 用 client-*，避免 daemon 的 get_log(source=file) 在回退时读到它。
        if (_host._clientLog)
        {
            return Path.Combine(
                _host._directory!,
                $"client-{Environment.ProcessId}-{date:yyyyMMdd}.log");
        }

        string profile = Profile.Resolve();
        string suffix = profile == "default" ? string.Empty : $".{profile}";
        return Path.Combine(
            _host._directory!,
            $"server{suffix}-{Environment.ProcessId}-{date:yyyyMMdd}.log");
    }

    internal void CleanupOldLogs()
    {
        if (_host._directory is null)
        {
            return;
        }

        DateTime cutoff = DateTime.Today.AddDays(-LogRing.RetentionDays);

        try
        {
            IEnumerable<string> files = Directory.EnumerateFiles(_host._directory, "server*.log*")
                .Concat(Directory.EnumerateFiles(_host._directory, "client*.log*"));

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
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }

    internal void WriteLoop()
    {
        if (_host._filePath is null || _host._pending is null)
        {
            return;
        }

        FileStream? stream = null;
        StreamWriter? writer = null;

        try
        {
            OpenWriter(ref stream, ref writer);

            foreach (string line in _host._pending.GetConsumingEnumerable())
            {
                try
                {
                    if (DateTime.Today != _host._fileDate)
                    {
                        _host._fileDate = DateTime.Today;
                        _host._filePath = BuildPath(_host._fileDate);
                        CloseWriter(ref stream, ref writer);
                        CleanupOldLogs();
                        OpenWriter(ref stream, ref writer);
                    }

                    if (writer is null || stream is null)
                    {
                        OpenWriter(ref stream, ref writer);
                    }

                    writer!.WriteLine(line);

                    if (stream!.Length >= LogRing.MaxFileBytes)
                    {
                        CloseWriter(ref stream, ref writer);
                        RotateFiles();
                        OpenWriter(ref stream, ref writer);
                    }
                }
                catch (Exception exception)
                {
                    LogSink.Debug($"忽略异常: {exception.Message}");
                    // 日志失败不能影响主流程；尝试重新打开文件。
                    CloseWriter(ref stream, ref writer);
                }
            }
        }
        catch (Exception exception)
        {
            // 文件不可用时静默降级为仅内存日志。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
        finally
        {
            CloseWriter(ref stream, ref writer);
        }
    }

    private void OpenWriter(ref FileStream? stream, ref StreamWriter? writer)
    {
        if (_host._filePath is null)
        {
            return;
        }

        stream = new FileStream(
            _host._filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
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
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        try
        {
            stream?.Dispose();
        }
        catch (Exception exception)
        {
            // 忽略。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        writer = null;
        stream = null;
    }

    private void RotateFiles()
    {
        if (_host._filePath is null)
        {
            return;
        }

        try
        {
            for (int index = LogRing.MaxBackups; index >= 1; index--)
            {
                string source = index == 1 ? _host._filePath : $"{_host._filePath}.{index - 1}";
                string target = $"{_host._filePath}.{index}";

                if (File.Exists(source))
                {
                    File.Move(source, target, overwrite: true);
                }
            }
        }
        catch (Exception exception)
        {
            // 轮转失败时继续写当前文件。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }

    public IReadOnlyList<string> ReadFileTail(int count)
    {
        if (_host._directory is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            string? newest = null;

            if (_host._filePath is not null && File.Exists(_host._filePath) && new FileInfo(_host._filePath).Length > 0)
            {
                newest = _host._filePath;
            }
            else
            {
                // 按角色 + profile 过滤，避免 get_log(source=file) 读到 proxy/其他 profile 的日志。
                string profile = Profile.Resolve();
                string prefix = _host._clientLog
                    ? "client-"
                    : profile == "default" ? "server-" : $"server.{profile}-";
                newest = Directory.EnumerateFiles(_host._directory, prefix + "*.log*")
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
            long start = Math.Max(0, stream.Length - LogRing.FileTailBytes);
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
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return Array.Empty<string>();
        }
    }
}
