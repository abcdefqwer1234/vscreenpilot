using System.Collections.Concurrent;

namespace VScreenPilot.Uia;

/// <summary>
/// 一代 UIA worker：单线程 MTA，所有 COM 调用串行。
/// 退休（超时 / 工厂失败）后标记 Abandoned，队列里尚未执行的动作直接失败，不能迟到执行。
/// </summary>
internal sealed class UiaGeneration
{
    private readonly Func<UiaService> _factory;
    private readonly UiaHost _host;

    public UiaGeneration(Func<UiaService> factory, UiaHost host)
    {
        _factory = factory;
        _host = host;
        Thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "vsp-uia-host",
        };
        Thread.SetApartmentState(ApartmentState.MTA);
    }

    public BlockingCollection<UiaHost.WorkItem> Queue { get; } = new();

    public Thread Thread { get; }

    public UiaService? Service { get; private set; }

    private volatile bool _abandoned;

    /// <summary>代际已退休：已排队但尚未执行的 WorkItem 必须丢弃，不能迟到执行。</summary>
    public bool Abandoned => _abandoned;

    public void Abandon() => _abandoned = true;

    public TaskCompletionSource Ready { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void Run()
    {
        UiaService? service = null;

        try
        {
            service = _factory();
            service.Host = _host;
            Service = service;
            Ready.TrySetResult();
        }
        catch (Exception exception)
        {
            Abandon();
            Ready.TrySetException(exception);

            foreach (UiaHost.WorkItem item in Queue.GetConsumingEnumerable())
            {
                item.Fail(exception);
            }

            return;
        }

        try
        {
            foreach (UiaHost.WorkItem item in Queue.GetConsumingEnumerable())
            {
                if (Abandoned)
                {
                    // 调用方已经收到 E_TIMEOUT 并退休了本代际：排队中的动作不能再执行，
                    // 否则会出现“报超时但后来还是点了”的幽灵操作。
                    item.Fail(_host.CallTimeout());
                    continue;
                }

                try
                {
                    item.Run(service);
                }
                catch (Exception exception)
                {
                    item.Fail(exception);
                }
            }
        }
        finally
        {
            service?.Dispose();
        }
    }
}
