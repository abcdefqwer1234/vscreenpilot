namespace VScreenPilot.Ipc;

/// <summary>
/// daemon 级 MCP 输入总缓冲预算。每个连接的单行增长都会占用额度，
/// 换行或连接关闭时释放，避免 N 个客户端同时各读 16MB。
/// </summary>
internal sealed class MessageBudget
{
    private readonly long _maxBytes;
    private long _inUse;

    public MessageBudget(long maxBytes)
    {
        _maxBytes = Math.Max(1, maxBytes);
    }

    public long InUse => Interlocked.Read(ref _inUse);

    public bool TryReserve(long bytes)
    {
        bytes = Math.Max(0, bytes);

        while (true)
        {
            long current = Interlocked.Read(ref _inUse);

            if (current + bytes > _maxBytes)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _inUse, current + bytes, current) == current)
            {
                return true;
            }
        }
    }

    public void Release(long bytes)
    {
        if (bytes <= 0)
        {
            return;
        }

        long current = Interlocked.Add(ref _inUse, -bytes);

        if (current < 0)
        {
            // 理论上不会发生；修正到 0，避免一个错误把预算永久锁死。
            Interlocked.Exchange(ref _inUse, 0);
        }
    }

    public static long ResolveMaxBytes()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_TOTAL_BUFFER_MB");

        if (int.TryParse(raw, out int mb))
        {
            mb = Math.Clamp(mb, 8, 512);
        }
        else
        {
            mb = 64;
        }

        return (long)mb * 1024 * 1024;
    }
}
