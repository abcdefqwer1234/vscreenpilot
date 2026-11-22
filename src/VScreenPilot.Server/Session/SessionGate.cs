namespace VScreenPilot.Session;

/// <summary>把 <see cref="SessionManager"/> 的读/写门收窄为工具层可用的接口。</summary>
internal sealed class SessionGate : VScreenPilot.Services.ISessionGate
{
    private readonly SessionManager _session;

    public SessionGate(SessionManager session)
    {
        _session = session;
    }

    public bool TryEnterRead(int timeoutMs) => _session.TryEnterRead(timeoutMs);

    public bool TryEnterExclusive(int timeoutMs) => _session.TryEnterExclusive(timeoutMs);

    public void ExitRead() => _session.ExitRead();

    public void ExitExclusive() => _session.ExitExclusive();
}
