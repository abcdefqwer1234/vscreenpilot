using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Session;

namespace VScreenPilot.Composition;

/// <summary>
/// 会话与传输层之间的接线：租约/屏幕状态的提供者由组合根注入，
/// 避免 SessionManager 反向依赖 MCP 层。
/// </summary>
internal static class SessionWiring
{
    public static void Wire(SessionManager session, LogRing log)
    {
        session.LeaseHolderProvider = () => session.Lease.HolderId;
        session.ScreenLeaseProvider = () => session.Lease.Describe();
        session.Lease.LogSink = log.Info;
        session.Lease.ClientCwdProvider = id => session.Clients.FindCwd(id);
    }
}
