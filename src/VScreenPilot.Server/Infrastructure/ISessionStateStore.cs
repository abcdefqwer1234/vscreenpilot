using VScreenPilot.Infrastructure;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>会话状态持久化的最小存储抽象，便于单测替换。</summary>
internal interface ISessionStateStore
{
    SessionState? Load();
    void Save(SessionState state);
    void Clear();
}
