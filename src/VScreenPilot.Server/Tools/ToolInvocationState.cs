using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Tools;

/// <summary>一次工具调用持有的租约/取消源/处理器任务，便于拆小 ExecuteAsync。</summary>
internal sealed class ToolInvocationState
{
    public WriteScheduler.Lease? Lease { get; set; }
    public IDisposable? ScreenLease { get; set; }
    public CancellationTokenSource? CallCts { get; set; }
    public bool CtsTransferred { get; set; }
    public Task<ToolResponse>? HandlerTask { get; set; }

    public void Apply(WriteScheduler.Lease? lease, IDisposable? screenLease, bool ctsTransferred)
    {
        Lease = lease;
        ScreenLease = screenLease;
        CtsTransferred = ctsTransferred;
    }
}
