using System.Text.Json.Nodes;
using VScreenPilot.Mcp;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>
/// 跨 profile 的输入租约。只在 VSP_FOCUS_LEASE=1 时启用：
/// 同一时刻只允许一个会话向前台注入鼠标/键盘，避免两个 profile 抢前台时输入错投。
/// 这是“输入串行化”，不是长期前台所有权。
/// </summary>
internal static class InputLease
{
    private const string MutexName = @"Local\VScreenPilot_InputLease";

    public static IDisposable? TryAcquire(int timeoutMs, string tool)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("VSP_FOCUS_LEASE"), "1",
                StringComparison.Ordinal))
        {
            return null;
        }

        var mutex = new Mutex(initiallyOwned: false, MutexName);

        try
        {
            if (mutex.WaitOne(Math.Max(1, timeoutMs)))
            {
                return new Handle(mutex);
            }
        }
        catch (AbandonedMutexException)
        {
            // 上一个持有者崩溃：把锁当作已获得。
            return new Handle(mutex);
        }

        mutex.Dispose();

        throw new ToolException(
            "E_FOCUS_OWNED",
            $"另一个会话持有输入租约，{tool} 在 {timeoutMs}ms 内未获得",
            new JsonObject { ["tool"] = tool, ["retry_after_ms"] = 500 },
            retryable: true,
            hint: "稍后重试；或关闭 VSP_FOCUS_LEASE / 串行执行两个 profile",
            stage: "focus");
    }

    private sealed class Handle : IDisposable
    {
        private Mutex? _mutex;

        public Handle(Mutex mutex) => _mutex = mutex;

        public void Dispose()
        {
            Mutex? mutex = Interlocked.Exchange(ref _mutex, null);

            if (mutex is null)
            {
                return;
            }

            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException exception){
                // 不是当前线程持有；忽略。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }

            mutex.Dispose();
        }
    }
}
