namespace VScreenPilot.Services;

/// <summary>会话读/写门：工具执行器在 handler 期间持有读门，写操作申请独占。</summary>
internal interface ISessionGate
{
    void EnterRead();
    bool TryEnterExclusive(int timeoutMs);
    void ExitRead();
    void ExitExclusive();
}
