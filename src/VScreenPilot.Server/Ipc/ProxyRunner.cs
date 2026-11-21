using System.Diagnostics;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Ipc;

/// <summary>已有实例时把 stdio 转发给 daemon；含 daemon 启动后的重试连接。</summary>
internal static class ProxyRunner
{
    public static int? RunProxy(string profile, bool allowScript)
    {
        // proxy 用独立文件，避免和 daemon 的 server-*.log 混在一起。
        using var proxyLog = new LogRing(clientLog: true);
        return StdioProxy.TryRun(profile, proxyLog, allowScript);
    }

    public static int? AttachToSpawnedDaemon(
        string profile, LogRing log, bool allowScript, Process child)
    {
        // 复用同一个 proxy LogRing：每次重试都新建会反复打开同一个文件、开新线程。
        using var proxyLog = new LogRing(clientLog: true);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (child.HasExited)
            {
                return null;
            }

            int? exit = StdioProxy.TryRun(profile, proxyLog, allowScript);

            if (exit is not null)
            {
                return exit;
            }

            Thread.Sleep(150);
        }

        log.Warn("独立 daemon 启动后未能在 15s 内连接");
        return null;
    }
}
