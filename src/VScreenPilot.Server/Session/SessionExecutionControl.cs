using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;

namespace VScreenPilot.Session;

/// <summary>把 <see cref="SessionManager"/> 收窄为工具层需要的执行控制。</summary>
internal sealed class SessionExecutionControl : IExecutionControl
{
    private readonly SessionManager _session;
    private readonly SessionGate _gate;

    public SessionExecutionControl(SessionManager session)
    {
        _session = session;
        _gate = new SessionGate(session);
    }

    public long Epoch => _session.Epoch;

    public ScreenLease Lease => _session.Lease;

    public ISessionGate Gate => _gate;

    public bool HasLiveOwnedApps() => _session.HasLiveOwnedApps();

    public IDisposable? UseClient(ClientInfo? client) =>
        client is null
            ? null
            : _session.Clients.Use(new ClientContext
            {
                Id = client.Id,
                Name = client.Name,
                Cwd = client.Cwd ?? Environment.CurrentDirectory,
                Roots = client.Roots,
                IncludeAllApps = client.IncludeAllApps,
            });

    public void SetCancellation(CancellationTokenSource? cts) => _session.SetCancellation(cts);

    public void PauseFocusGuard(int leaseMs = 0) => _session.PauseFocusGuard(leaseMs);

    public void ExtendLease(string clientId, int durationMs) =>
        _session.Lease.Extend(clientId, durationMs);
}
