using VScreenPilot.Composition;
using VScreenPilot.Infrastructure;

namespace VScreenPilot.Ipc;

/// <summary>独立 daemon 入口：实现已并入 <see cref="ServerHost.RunDaemon"/>。</summary>
internal static class DaemonHost
{
    public static int RunStandalone(string profile, LogRing log, bool allowScript) =>
        ServerHost.RunDaemon(profile, log, allowScript);
}
