using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Services;

/// <summary>工具执行控制：租约、epoch、取消与前台保护；不暴露 SessionManager。</summary>
internal interface IExecutionControl
{
    long Epoch { get; }
    ScreenLease Lease { get; }
    ISessionGate Gate { get; }
    bool HasLiveOwnedApps();
    IDisposable? UseClient(ClientInfo? client);
    void SetCancellation(CancellationTokenSource? cts);
    CancellationTokenSource? CurrentCancellationSource { get; }
    void PauseFocusGuard(int leaseMs = 0);
    void ExtendLease(string clientId, int durationMs);
}
