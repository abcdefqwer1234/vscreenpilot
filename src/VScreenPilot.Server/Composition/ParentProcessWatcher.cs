using VScreenPilot.Infrastructure;
using VScreenPilot.Session;

namespace VScreenPilot.Composition;

/// <summary>启动器退出后触发清理；仍有共享客户端时继续作为 daemon 运行。</summary>
internal static class ParentProcessWatcher
{
    /// <summary>
    /// 启动器被杀时也要走统一清理，避免虚拟屏和子进程残留。
    /// 这是进程事件监听，不是超时看门狗。
    /// </summary>
    public static void Start(int parentPid, SessionManager session, LogRing log, Func<bool> hasSharedClients)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // 父进程在进入监听前已经退出。
            }
            catch (Exception exception)
            {
                log.Warn($"父进程监听失败 (pid={parentPid}): {exception.Message}");
                return;
            }

            log.Info($"父进程已退出 (pid={parentPid})，开始清理");

            if (hasSharedClients())
            {
                log.Warn("父进程退出但仍有共享客户端，继续作为 daemon 运行");
                return;
            }

            try
            {
                session.Dispose();
            }
            catch (Exception exception)
            {
                log.Error("父进程退出后的清理失败", exception);
            }

            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "ParentWatcher",
        };

        thread.Start();
    }
}
