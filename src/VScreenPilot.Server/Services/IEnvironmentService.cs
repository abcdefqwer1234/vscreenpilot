using System.Text.Json.Nodes;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>环境查询/释放与屏幕租约摘要。</summary>
internal interface IEnvironmentService
{
    EnvironmentView Get(bool ensure);
    ReleaseReport Release(ReleaseRequest request);
    JsonObject DescribeLease();
    bool ReleaseLease(string clientId);
    int ActiveClientCount { get; }
}

internal sealed class EnvironmentToolService : IEnvironmentService
{
    private readonly SessionManager _session;

    public EnvironmentToolService(SessionManager session) => _session = session;

    public EnvironmentView Get(bool ensure) => _session.GetEnvironment(ensure);

    public ReleaseReport Release(ReleaseRequest request) =>
        _session.ReleaseEnvironment(request.KeepDisplay, request.OnDirty, request.AllowDataLoss);

    public JsonObject DescribeLease() => _session.Lease.Describe();

    public bool ReleaseLease(string clientId) => _session.Lease.ForceRelease(clientId);

    public int ActiveClientCount => _session.Clients.ActiveCount;
}
