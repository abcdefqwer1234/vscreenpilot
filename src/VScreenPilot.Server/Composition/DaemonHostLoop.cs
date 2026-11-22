using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Options;

namespace VScreenPilot.Composition;

/// <summary>独立 daemon 的等待循环与退出信号；从 ServerHost 拆出。</summary>
internal static class DaemonHostLoop
{
    public static void RegisterStopSignal(ManualResetEventSlim stop)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Set();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.Set();
        };
    }

    public static int Run(ServerHost host, ManualResetEventSlim stop, LogRing log)
    {
        int idleMs = VspOptions.FromEnvironment().DaemonIdleMs;
        bool hadClient = false;
        DateTime? idleSince = DateTime.UtcNow;

        while (!stop.Wait(100))
        {
            int active = host.ActiveClients;

            if (active > 0)
            {
                hadClient = true;
                idleSince = null;
                continue;
            }

            idleSince ??= DateTime.UtcNow;

            // 首次启动给客户端留 10s 连接窗口；有过客户端后严格按配置退出。
            int graceMs = hadClient ? idleMs : Math.Max(idleMs, 10_000);

            if (graceMs == 0 ||
                DateTime.UtcNow - idleSince.Value >= TimeSpan.FromMilliseconds(graceMs))
            {
                break;
            }
        }

        log.Info("独立 daemon 进入退出流程");
        return 0;
    }
}
