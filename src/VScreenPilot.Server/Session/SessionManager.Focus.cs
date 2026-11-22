using VScreenPilot.Apps;
using VScreenPilot.Interop;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    private readonly FocusGuard _focus;

    public void StartFocusGuard(IntPtr preferredUserForeground = default) =>
        _focus.Start(preferredUserForeground);

    public void PauseFocusGuard(int leaseMs = 0) => _focus.Pause(leaseMs);

    public void ResumeFocusGuard() => _focus.Resume();

    private void StopFocusGuard() => _focus.Stop();

    private bool IsTrackedWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        // 只有当前屏幕租约持有者的 apps 算“本会话应用”；没有租约时
        // 不把其他客户端的窗口误当成本会话窗口。
        string? holder = LeaseHolderProvider?.Invoke();

        if (string.IsNullOrWhiteSpace(holder))
        {
            return false;
        }

        lock (_gate)
        {
            return _apps.Any(app =>
                string.Equals(app.OwnerClientId, holder, StringComparison.Ordinal) &&
                app.Pid == (int)pid &&
                TrackedAppTracker.IsLive(app));
        }
    }

    private bool IsAnyClientWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);

        lock (_gate)
        {
            return _apps.Any(app => app.Pid == (int)pid && TrackedAppTracker.IsLive(app));
        }
    }
}
