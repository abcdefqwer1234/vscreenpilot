using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>
/// 写操作调度：只读观察（snapshot/find/capture/wait/assert）不排队，
/// 只有会改变应用状态的工具串行执行。这样长等待不会再让“看一眼屏幕”失败。
/// </summary>
internal sealed class WriteScheduler : IDisposable
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly int _bypassAfterMs;
    private string? _holder;
    private long _acquiredTicks;
    private int _disposed;

    public WriteScheduler(int bypassAfterMs = 5000)
    {
        _bypassAfterMs = Math.Max(0, bypassAfterMs);
    }

    public string? Holder => Volatile.Read(ref _holder);

    public long HeldMs
    {
        get
        {
            long acquired = Interlocked.Read(ref _acquiredTicks);
            // TickCount64 的单位是毫秒，不要再套 TimeSpan.FromTicks。
            return acquired == 0 ? 0 : Math.Max(0, Environment.TickCount64 - acquired);
        }
    }

    public async Task<Lease> AcquireAsync(
        string tool, TimeSpan timeout, CancellationToken cancellationToken, bool bypass = false)
    {
        if (await _write.WaitAsync(timeout, cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Exchange(ref _holder, tool);
            Interlocked.Exchange(ref _acquiredTicks, Environment.TickCount64);
            return new Lease(this, held: true);
        }

        // release 只在写锁已经卡住 >=5s 时才旁路；正常 click/type_text 未结束时应返回 E_BUSY，
        // 避免 release 与写操作并发清理窗口。
        if (bypass && HeldMs >= _bypassAfterMs)
        {
            return new Lease(this, held: false);
        }

        string holder = Holder ?? "unknown";

        throw new ToolException(
            "E_BUSY",
            $"另一个写操作正在执行（holder={holder}），本次未排队成功",
            new JsonObject
            {
                ["holder"] = holder,
                ["held_ms"] = HeldMs,
                ["retry_after_ms"] = 250,
            },
            retryable: true,
            hint: "按 retry_after_ms 重试；只读工具（snapshot/find/capture）不需要等待写锁",
            stage: "gate");
    }

    /// <summary>卡死的 handler 仍在操作 UI：锁必须等它真正结束再释放。</summary>
    public static void ReleaseWhen(Task task, Lease lease)
    {
        _ = task.ContinueWith(
            _ => lease.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _write.Dispose();
    }

    internal sealed class Lease : IDisposable
    {
        private readonly WriteScheduler _owner;
        private int _released;

        internal Lease(WriteScheduler owner, bool held)
        {
            _owner = owner;
            Held = held;
        }

        public bool Held { get; }

        public void Dispose()
        {
            if (!Held || Interlocked.Exchange(ref _released, 1) != 0)
            {
                return;
            }

            Interlocked.Exchange(ref _owner._holder, null);
            Interlocked.Exchange(ref _owner._acquiredTicks, 0);

            try
            {
                _owner._write.Release();
            }
            catch (ObjectDisposedException exception)
            {
                // 进程退出时忽略。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }
    }
}
