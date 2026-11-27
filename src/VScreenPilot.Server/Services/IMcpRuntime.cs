using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Session;

namespace VScreenPilot.Services;

/// <summary>
/// MCP 传输层所需的会话窄口：身份、租约、指标与客户端生命周期。
/// 工具 handler 只依赖本接口，避免直接依赖 SessionManager。
/// </summary>
internal interface IMcpRuntime
{
    string ProfileName { get; }
    ScreenLease Lease { get; }
    MetricsRegistry Metrics { get; }
    void Enter(ClientInfo client);
    void Leave(string clientId);
    bool Release(ClientInfo? client, bool includeApps);
    void DropRefs(string clientId);
}

internal sealed class McpRuntime : IMcpRuntime
{
    private readonly SessionManager _session;

    public McpRuntime(SessionManager session) => _session = session;

    public string ProfileName => _session.ProfileName;

    public ScreenLease Lease => _session.Lease;

    public MetricsRegistry Metrics => _session.Metrics;

    public void Enter(ClientInfo client)
    {
        ClientContext context = _session.Clients.Resolve(client);
        _session.Clients.Enter(context);
        _session.ClientOps.ClaimRecoveredApps(context);
    }

    public void Leave(string clientId) => _session.Clients.Leave(clientId);

    public bool Release(ClientInfo? client, bool includeApps) =>
        _session.ClientOps.ReleaseClient(Resolve(client), includeApps);

    public void DropRefs(string clientId) => _session.ClientOps.DropClientRefs(clientId);

    // 释放路径只做查找 / 兜底，不登记：客户端断开后的延后清理不能把 ActiveCount 加回来。
    private ClientContext? Resolve(ClientInfo? client) =>
        client is null ? null : _session.Clients.Resolve(client);
}
