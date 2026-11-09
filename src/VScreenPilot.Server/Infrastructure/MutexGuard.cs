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
                // Global\ 权限不足时尝试 Local\。
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
        catch
        {
            // 忽略。
        }

        _mutex.Dispose();
        _mutex = null;
    }
}
