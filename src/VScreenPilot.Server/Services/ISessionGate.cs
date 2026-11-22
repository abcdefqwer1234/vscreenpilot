namespace VScreenPilot.Services;

/// <summary>
/// 会话读/写门：普通执行在 handler 期间持读锁，写操作（release）独占。
/// 读锁统一走带超时的 <see cref="TryEnterRead"/>，避免 release 持写锁时无限阻塞。
/// </summary>
internal interface ISessionGate
{
    bool TryEnterRead(int timeoutMs);
    bool TryEnterExclusive(int timeoutMs);
    void ExitRead();
    void ExitExclusive();
}
