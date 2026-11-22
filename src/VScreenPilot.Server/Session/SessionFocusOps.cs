using VScreenPilot.Apps;
using VScreenPilot.Interop;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Session;

/// <summary>FocusOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionFocusOps
{
    internal readonly SessionManager _host;

    public SessionFocusOps(SessionManager host) => _host = host;

    public void StartFocusGuard(IntPtr preferredUserForeground = default) =>
        _host.FocusGuard.Start(preferredUserForeground);

    public void PauseFocusGuard(int leaseMs = 0) => _host.FocusGuard.Pause(leaseMs);

    public void ResumeFocusGuard() => _host.FocusGuard.Resume();

    internal void StopFocusGuard() => _host.FocusGuard.Stop();

    internal bool IsTrackedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        // 只有当前屏幕租约持有者的 apps 算“本会话应用”；没有租约时
        // 不把其他客户端的窗口误当成本会话窗口。
        string? holder = _host.LeaseHolderProvider?.Invoke();

        if (string.IsNullOrWhiteSpace(holder))
        {
            return false;
        }

        lock (_host._gate)
        {
            return _host._apps.Any(app =>
                string.Equals(app.OwnerClientId, holder, StringComparison.Ordinal) &&
                app.Pid == (int)pid &&
                TrackedAppTracker.IsLive(app));
        }
    }

    internal bool IsAnyClientWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        lock (_host._gate)
        {
            return _host._apps.Any(app => app.Pid == (int)pid && TrackedAppTracker.IsLive(app));
        }
    }
}
