namespace VScreenPilot.Infrastructure;

/// <summary>全局命名互斥体，保证同一会话只有一个 Server 实例。</summary>
public sealed class MutexGuard : IDisposable
{
    private Mutex? _mutex;

    public bool Held => _mutex is not null;

    public bool TryAcquire(string name, out string? error)
    {
        error = null;

        foreach (string candidate in new[] { $@"Global\{name}", $@"Local\{name}" })
        {
            try
            {
                var mutex = new Mutex(initiallyOwned: false, candidate);
                bool acquired;

                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    // 上一个持有者崩溃：互斥体已归当前进程。
                    acquired = true;
                }

                if (acquired)
                {
                    _mutex = mutex;
                    return true;
                }

                mutex.Dispose();
                error = $"已有活跃实例持有互斥体 {candidate}";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                // default profile 不再回退 Local：否则会出现两个 daemon/两块屏。
                if (string.Equals(name, Profile.MutexName("default"), StringComparison.OrdinalIgnoreCase))
                {
                    error = $"无法访问全局互斥体 {candidate}（权限不足；可能由另一个用户/会话持有）";
                    return false;
                }

                // 非默认 profile 仍保留 Global 权限不足时尝试 Local 的兼容行为。
                continue;
            }
            catch (Exception exception)
            {
                error = $"创建互斥体 {candidate} 失败: {exception.Message}";
                return false;
            }
        }

        error = "无法创建全局或局部互斥体";
        return false;
    }

    public void Dispose()
    {
        if (_mutex is null)
        {
            return;
        }

        try
        {
            _mutex.ReleaseMutex();
        }
        catch (Exception exception)
        {
            // 忽略。
            _ = exception;
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
