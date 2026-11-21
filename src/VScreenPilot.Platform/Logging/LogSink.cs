using System.Diagnostics;

namespace VScreenPilot.Platform.Logging;

/// <summary>
/// 跨程序集的轻量诊断出口。默认写 <see cref="Trace"/>；宿主启动时调用
/// <see cref="Configure"/> 注册文件 listener，保证 Release 下也能落盘。
/// 不引入日志抽象、不改变主日志（LogRing）布局。
/// </summary>
internal static class LogSink
{
    private static readonly object Gate = new();
    private static bool _configured;

    /// <summary>注册 <c>trace-&lt;profile&gt;.log</c>；重复调用只生效一次。</summary>
    public static void Configure(string path)
    {
        lock (Gate)
        {
            if (_configured)
            {
                return;
            }

            try
            {
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                Trace.Listeners.Add(new TextWriterTraceListener(path, "vsp-trace"));
                Trace.AutoFlush = true;
                _configured = true;
            }
            catch (Exception exception)
            {
                // 诊断出口自身失败不能影响主流程；退回只写 Trace。
                Trace.WriteLine($"LogSink: 无法写入 {path}: {exception.Message}");
            }
        }
    }

    public static void Debug(string message) => Write("DEBUG", message);

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    private static void Write(string level, string message)
    {
        try
        {
            Trace.WriteLine($"{DateTime.Now:yyyy-MM-ddTHH:mm:ss.fff} {level} {message}");
        }
        catch (Exception)
        {
            // Trace listener 可能已释放；诊断失败静默，不改变业务行为。
        }
    }
}
