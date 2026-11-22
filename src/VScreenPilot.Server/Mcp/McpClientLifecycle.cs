using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Services;

namespace VScreenPilot.Mcp;

/// <summary>连接级客户端登记与断开清理（租约、refs、窗口按 force 收回）。</summary>
internal sealed class McpClientLifecycle
{
    private readonly IMcpRuntime _runtime;
    private readonly LogRing _log;
    private readonly Func<int> _inflight;
    private readonly ClientInfo? _client;

    public McpClientLifecycle(IMcpRuntime runtime, LogRing log, Func<int> inflight, ClientInfo? client)
    {
        _runtime = runtime;
        _log = log;
        _inflight = inflight;
        _client = client;
    }

    public void Begin(ClientInfo effective) => _runtime.Enter(effective);

    /// <summary>
    /// 连接结束：先释放租约（若自己的 handler 仍在跑或别人持有，返回 false）。
    /// refs/artifacts 可以立即清；apps/窗口必须等租约和 handler 都空闲后再按 force 清理。
    /// </summary>
    public void End(string clientId)
    {
        bool leaseFree = _runtime.Lease.ReleaseHolderIfIdle(clientId);
        bool handlersIdle = _inflight() == 0;

        try
        {
            _runtime.DropRefs(clientId);

            if (leaseFree && handlersIdle && _runtime.Release(_client, includeApps: true))
            {
                // 已立即完成清理。
            }
            else
            {
                Schedule(clientId);
            }
        }
        catch (Exception exception)
        {
            // 清理失败不能影响连接计数/trace 释放；稍后由 daemon 维护或重试兜底。
            _log.Warn($"客户端 {clientId} 清理失败: {exception.Message}");
            Schedule(clientId);
        }

        _runtime.Leave(clientId);
    }

    /// <summary>
    /// 客户端已断开但仍有在途 handler/租约时，等真正空闲后再按 force 清理窗口。
    /// daemon 退出时会做全量清理，这里是长期运行 daemon 的补充。
    /// </summary>
    private void Schedule(string clientId)
    {
        _ = Task.Run(async () =>
        {
            DateTime deadline = DateTime.UtcNow.AddMinutes(30);

            while (DateTime.UtcNow < deadline)
            {
                if (_inflight() == 0 && _runtime.Lease.ReleaseHolderIfIdle(clientId))
                {
                    try
                    {
                        if (_runtime.Release(_client, includeApps: true))
                        {
                            return;
                        }
                    }
                    catch (Exception exception)
                    {
                        _log.Warn($"客户端 {clientId} 延后清理失败: {exception.Message}");
                    }
                }

                await Task.Delay(250).ConfigureAwait(false);
            }

            _log.Warn($"客户端 {clientId} 的延后清理超过 30 分钟，留待 daemon 退出处理");
        });
    }
}
