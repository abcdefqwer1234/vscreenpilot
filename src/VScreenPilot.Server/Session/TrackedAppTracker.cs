using System.Diagnostics;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Windows;

namespace VScreenPilot.Session;

/// <summary>跟踪应用所需的最小宿主接口。</summary>
internal interface ITrackedAppHost
{
    TrackedAppRegistry Apps { get; }
    object Gate { get; }
    bool IsOwned(TrackedApp app);
    LogRing Log { get; }
}

/// <summary>已接管应用的存活判定、清理与 Office 启动壳重定位。</summary>
internal sealed class TrackedAppTracker
{
    private readonly ITrackedAppHost _host;

    public TrackedAppTracker(ITrackedAppHost host) => _host = host;

    /// <summary>
    /// 条目仍可用：窗口存在且仍属于登记时的进程。
    /// 窗口关闭后 PID 可能被系统复用，只比较 PID 会把外来窗口误判成本会话应用。
    /// </summary>
    public static bool IsLive(TrackedApp app)
    {
        if (app.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(app.Hwnd))
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(app.Hwnd, out uint ownerPid);
        return ownerPid == (uint)app.Pid;
    }

    public TrackedApp? LastLive()
    {
        lock (_host.Gate)
        {
            return _host.Apps.LastOrDefault(app => _host.IsOwned(app) && IsLive(app))
                   ?? TryRelocateLast();
        }
    }

    /// <summary>清理已死超过 3s 的条目，避免 apps[] 长期残留（N5）。</summary>
    public void Prune()
    {
        lock (_host.Gate)
        {
            DateTime now = DateTime.UtcNow;

            for (int index = _host.Apps.Count - 1; index >= 0; index--)
            {
                TrackedApp app = _host.Apps[index];

                if (IsLive(app))
                {
                    app.DeadSince = null;
                    continue;
                }

                app.DeadSince ??= now;

                if ((now - app.DeadSince.Value).TotalSeconds >= 3)
                {
                    _host.Apps.RemoveAt(index);
                }
            }
        }
    }

    public TrackedApp? FindLive(int? pid)
    {
        if (pid is null)
        {
            return LastLive();
        }

        lock (_host.Gate)
        {
            TrackedApp? live = _host.Apps.LastOrDefault(
                app => app.Pid == pid.Value && _host.IsOwned(app) && IsLive(app));
            if (live is not null)
            {
                return live;
            }

            TrackedApp? dead = _host.Apps.LastOrDefault(
                app => app.Pid == pid.Value && _host.IsOwned(app));
            return dead is not null && TryRelocate(dead) ? dead : null;
        }
    }

    /// <summary>
    /// Office 等应用会由启动壳进程交接给新进程，原 pid/hwnd 随即失效。
    /// 按 exe 路径 + 启动时间把死条目重定位到新的顶层窗口，避免成功启动后立即 E_NO_TARGET。
    /// </summary>
    private TrackedApp? TryRelocateLast()
    {
        foreach (TrackedApp app in _host.Apps.Snapshot().AsEnumerable().Reverse().Where(_host.IsOwned))
        {
            if (TryRelocate(app))
            {
                return app;
            }
        }

        return null;
    }

    private bool TryRelocate(TrackedApp app)
    {
        if (!_host.IsOwned(app) || string.IsNullOrWhiteSpace(app.ExePath))
        {
            return false;
        }

        WindowInfo? candidate = WindowManager.ListTopLevel()
            .Where(window => !_host.Apps.Any(other => other.Hwnd == window.Hwnd))
            .Where(window => string.Equals(
                WindowManager.GetProcessPath(window.Pid), app.ExePath, StringComparison.OrdinalIgnoreCase))
            .Where(window => ProcessStartedAfter(window.Pid, app.StartedAt))
            .OrderByDescending(window => window.Area)
            .FirstOrDefault();

        if (candidate is null)
        {
            return false;
        }

        _host.Log.Warn(
            $"应用 pid={app.Pid} 已退出，按 exe 重定位到 pid={candidate.Pid}");
        app.Pid = candidate.Pid;
        app.Hwnd = candidate.Hwnd;
        app.Title = candidate.Title;
        app.Placement = WindowManager.GetPlacement(candidate.Hwnd);
        app.PlacementSaved = true;
        return true;
    }

    private static bool ProcessStartedAfter(int pid, DateTime startedAt)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime() >= startedAt.ToUniversalTime().AddSeconds(-2);
        }
        catch
        {
            return false;
        }
    }
}
