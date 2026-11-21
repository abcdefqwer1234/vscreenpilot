namespace VScreenPilot.Session;

/// <summary>
/// 会话读写闸：只读观察（snapshot/capture/find/…）与 release 的互斥。
/// 保留原 `ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion)` 语义：
/// 递归获取会抛异常，避免把“锁内回调”这类死锁写进代码。
/// </summary>
internal sealed class SessionGuard : IDisposable
{
    private readonly ReaderWriterLockSlim _lock =
        new(LockRecursionPolicy.NoRecursion);

    public void EnterRead() => _lock.EnterReadLock();

    public void ExitRead()
    {
        if (_lock.IsReadLockHeld)
        {
            _lock.ExitReadLock();
        }
    }

    public bool TryEnterExclusive(int milliseconds) =>
        _lock.TryEnterWriteLock(Math.Max(0, milliseconds));

    public void ExitExclusive()
    {
        if (_lock.IsWriteLockHeld)
        {
            _lock.ExitWriteLock();
        }
    }

    public void Dispose() => _lock.Dispose();
}
