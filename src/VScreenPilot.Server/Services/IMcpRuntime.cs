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

    public void Enter(ClientInfo client) => _session.Clients.Enter(ToContext(client));

    public void Leave(string clientId) => _session.Clients.Leave(clientId);

    public bool Release(ClientInfo? client, bool includeApps) =>
        _session.ClientOps.ReleaseClient(Resolve(client), includeApps);

    public void DropRefs(string clientId) => _session.ClientOps.DropClientRefs(clientId);

    private ClientContext? Resolve(ClientInfo? client)
    {
        if (client is null)
        {
            return null;
        }

        ClientContext? existing = _session.Clients.All
            .FirstOrDefault(item => string.Equals(item.Id, client.Id, StringComparison.Ordinal));
        return existing ?? ToContext(client);
    }

    private static ClientContext ToContext(ClientInfo client) => new()
    {
        Id = client.Id,
        Name = client.Name,
        Cwd = client.Cwd ?? Environment.CurrentDirectory,
        Roots = client.Roots,
        IncludeAllApps = client.IncludeAllApps,
    };
}
