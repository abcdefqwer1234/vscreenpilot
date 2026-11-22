using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Tools;

/// <summary>
/// 工具调用的资源门：先拿屏幕租约，再排队写锁；任一步失败都必须回滚已拿到的资源。
/// 单独的静态类便于用 fake 的 <see cref="ScreenLease"/>/<see cref="WriteScheduler"/> 做单测。
/// </summary>
internal static class InvocationGate
{
    /// <summary>
    /// 获取屏幕租约与写锁。失败（E_BUSY、E_SCREEN_LEASED、取消）时释放已获取的资源，
    /// 不把半成品元组交给调用方。
    /// </summary>
    public static async Task<(WriteScheduler.Lease? Lease, IDisposable? ScreenLease)> AcquireAsync(
        ScreenLease screenLease,
        WriteScheduler scheduler,
        string clientId,
        string tool,
        bool requiresScreenLease,
        bool mutates,
        TimeSpan writeTimeout,
        bool bypassWriteLock,
        CancellationToken cancellationToken)
    {
        WriteScheduler.Lease? lease = null;
        IDisposable? screen = null;

        try
        {
            if (requiresScreenLease)
            {
                screen = screenLease.Acquire(clientId, tool);
            }

            if (mutates)
            {
                lease = await scheduler.AcquireAsync(
                    tool, writeTimeout, cancellationToken, bypass: bypassWriteLock).ConfigureAwait(false);
            }

            return (lease, screen);
        }
        catch (Exception)
        {
            // 释放顺序与获取相反：先写锁，后屏幕租约。
            lease?.Dispose();
            screen?.Dispose();
            throw;
        }
    }
}
