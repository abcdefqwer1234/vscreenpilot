using System.Diagnostics;
using System.Text;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Platform.Windows;

internal sealed record WindowInfo(
    IntPtr Hwnd,
    int Pid,
    string Title,
    string ClassName,
    NativeMethods.RECT Rect,
    bool Visible,
    bool Owned)
{
    public int Width => Rect.Right - Rect.Left;

    public int Height => Rect.Bottom - Rect.Top;

    public long Area => (long)Math.Max(0, Width) * Math.Max(0, Height);
}

internal static class WindowManager
{
    public static IReadOnlyList<WindowInfo> ListTopLevel()
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                string title = GetTitle(hwnd);

                _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                NativeMethods.GetWindowRect(hwnd, out var rect);

                var className = new StringBuilder(256);
                _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

                bool owned = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero;

                result.Add(new WindowInfo(
                    hwnd, (int)pid, title, className.ToString(), rect,
                    Visible: true, Owned: owned));
            }
            catch (Exception exception)
            {
                // 枚举过程中的个别窗口失败不影响整体。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>
    /// 返回所有标题匹配的顶层窗口，按“完全相等优先、面积降序”排序。
    /// 调用方在多候选时应报歧义，而不是随手接管第一个。
    /// </summary>
    public static IReadOnlyList<WindowInfo> FindByTitleCandidates(
        string titleSubstring, Func<WindowInfo, bool>? filter = null)
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                string title = GetTitle(hwnd);
                if (string.IsNullOrWhiteSpace(title) ||
                    !title.Contains(titleSubstring, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                WindowInfo info = Describe(hwnd, title);
                if (filter is null || filter(info))
                {
                    result.Add(info);
                }
            }
            catch (Exception exception)
            {
                // 单个窗口枚举失败不影响其他候选。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }

            return true;
        }, IntPtr.Zero);

        return result
            .OrderByDescending(item => item.Title.Equals(titleSubstring, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Area)
            .ToList();
    }

    public static WindowInfo? FindMainWindow(int pid)
    {
        WindowInfo? bestWithTitle = null;
        WindowInfo? bestUnowned = null;
        WindowInfo? bestAny = null;
        long titleArea = -1, unownedArea = -1, anyArea = -1;

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
                if (ownerPid != (uint)pid)
                {
                    return true;
                }

                string title = GetTitle(hwnd);
                WindowInfo info = Describe(hwnd, title);
                bool owned = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero;

                if (info.Area > anyArea)
                {
                    anyArea = info.Area;
                    bestAny = info;
                }

                if (!owned)
                {
                    if (info.Area > unownedArea)
                    {
                        unownedArea = info.Area;
                        bestUnowned = info;
                    }

                    if (!string.IsNullOrWhiteSpace(title) && info.Area > titleArea)
                    {
                        titleArea = info.Area;
                        bestWithTitle = info;
                    }
                }
            }
            catch (Exception exception)
            {
                // 枚举中的个别窗口失败不影响整体。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }

            return true;
        }, IntPtr.Zero);

        return bestWithTitle ?? bestUnowned ?? bestAny;
    }

    private static WindowInfo Describe(IntPtr hwnd, string title)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

        return new WindowInfo(
            hwnd, (int)pid, title, className.ToString(), rect,
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero);
    }

    public static WindowInfo? FromHandle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

        return new WindowInfo(
            hwnd, (int)pid, GetTitle(hwnd), className.ToString(), rect,
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero);
    }

    public static string GetTitle(IntPtr hwnd)
    {
        // 不用 GetWindowTextW：目标线程挂起时会同步等 WM_GETTEXT。
        var buffer = new StringBuilder(512);
        IntPtr result = NativeMethods.SendMessageTimeoutW(
            hwnd, NativeMethods.WmGetText, (IntPtr)buffer.Capacity, buffer,
            NativeMethods.SmtoAbortIfHung, 120, out _);

        return result == IntPtr.Zero ? string.Empty : buffer.ToString();
    }

    public static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static int GetProcessId(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    public static IReadOnlyList<WindowInfo> ListForProcess(int pid) =>
        ListTopLevel().Where(window => window.Pid == pid).ToList();

    /// <summary>
    /// 枚举属于该进程、且 owner 链指向主窗口的可见窗口。
    /// 覆盖 Office 的 bosa_sdm_msword、经典 #32770 与 #32768 弹出菜单——
    /// 这些窗口通常不在主窗口的 UIA 树里，但仍然是可操作的顶层窗口。
    /// </summary>
    public static IReadOnlyList<WindowInfo> ListOwnedWindows(int pid, IntPtr mainHwnd)
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            try
            {
                if (hwnd == mainHwnd || !NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
                if (ownerPid != (uint)pid)
                {
                    return true;
                }

                var className = new StringBuilder(256);
                _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

                bool owned = false;
                IntPtr current = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner);

                for (int depth = 0; depth < 8 && current != IntPtr.Zero; depth++)
                {
                    if (current == mainHwnd)
                    {
                        owned = true;
                        break;
                    }

                    current = NativeMethods.GetWindow(current, NativeMethods.GwOwner);
                }

                bool popupMenu = className.ToString() == "#32768";

                if (owned || popupMenu)
                {
                    result.Add(Describe(hwnd, GetTitle(hwnd)));
                }
            }
            catch (Exception exception)
            {
                // 单个窗口失败不影响枚举。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static IntPtr GetActivePopup(IntPtr hwnd)
    {
        IntPtr popup = NativeMethods.GetLastActivePopup(hwnd);
        return popup != IntPtr.Zero && popup != hwnd && NativeMethods.IsWindowVisible(popup)
            ? popup
            : hwnd;
    }

    public static void SwitchTo(IntPtr hwnd) => NativeMethods.SwitchToThisWindow(hwnd, false);

    public static bool ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        if (NativeMethods.IsIconic(hwnd))
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        }

        if (NativeMethods.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        // 目标 UI 线程挂起时 AttachThreadInput 会把服务端一起拖死；
        // 先用 WM_NULL + SMTO_ABORTIFHUNG 做 120ms 健康检查。
        if (!IsWindowResponsive(hwnd))
        {
            return false;
        }

        return AttachAndSetForeground(hwnd);
    }

    private static bool IsWindowResponsive(IntPtr hwnd) =>
        NativeMethods.SendMessageTimeoutW(
            hwnd, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero,
            NativeMethods.SmtoAbortIfHung, 120, out _) != IntPtr.Zero;

    private static bool AttachAndSetForeground(IntPtr hwnd)
    {
        uint currentThread = NativeMethods.GetCurrentThreadId();
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        uint foregroundThread = foreground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(foreground, out _);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);

        bool attachedForeground = false;
        bool attachedTarget = false;

        try
        {
            // 只有附加到当前前台线程才能绕过前台锁。
            attachedForeground = TryAttach(currentThread, foregroundThread);
            attachedTarget = targetThread != foregroundThread &&
                             TryAttach(currentThread, targetThread);

            return NativeMethods.SetForegroundWindow(hwnd);
        }
        finally
        {
            if (attachedTarget)
            {
                NativeMethods.AttachThreadInput(currentThread, targetThread, false);
            }

            if (attachedForeground)
            {
                NativeMethods.AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    private static bool TryAttach(uint currentThread, uint targetThread) =>
        targetThread != 0 &&
        targetThread != currentThread &&
        NativeMethods.AttachThreadInput(currentThread, targetThread, true);

    public static NativeMethods.WINDOWPLACEMENT GetPlacement(IntPtr hwnd)
    {
        var placement = new NativeMethods.WINDOWPLACEMENT
        {
            length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>(),
        };

        NativeMethods.GetWindowPlacement(hwnd, ref placement);
        return placement;
    }

    public static bool SetPlacement(IntPtr hwnd, NativeMethods.WINDOWPLACEMENT placement)
    {
        placement.length = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.WINDOWPLACEMENT>();
        return NativeMethods.SetWindowPlacement(hwnd, ref placement);
    }

    public static void MoveAndMaximize(IntPtr hwnd, int x, int y, int width, int height)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwMaximize);
    }

    /// <summary>移到目标矩形但不最大化；用于“放到虚拟屏、保持窗口形态”的附着场景。</summary>
    public static void MoveResize(IntPtr hwnd, int x, int y, int width, int height)
    {
        NativeMethods.ShowWindow(hwnd, NativeMethods.SwRestore);
        NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, width, height,
            NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    /// <summary>
    /// DWM 可见边框；失败时退回 GetWindowRect。
    /// 用于 fit=clamp，避免把 DWM 的隐形阴影算进窗口尺寸。
    /// </summary>
    public static (int Left, int Top, int Width, int Height) GetVisibleBounds(IntPtr hwnd)
    {
        if (NativeMethods.DwmGetWindowAttribute(
                hwnd, 9, out NativeMethods.RECT frame,
                System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.RECT>()) == 0 &&
            frame.Right > frame.Left && frame.Bottom > frame.Top)
        {
            return (frame.Left, frame.Top, frame.Right - frame.Left, frame.Bottom - frame.Top);
        }

        WindowInfo? info = FromHandle(hwnd);
        return info is null
            ? (0, 0, 0, 0)
            : (info.Rect.Left, info.Rect.Top, info.Width, info.Height);
    }

    public static bool IsEnabled(IntPtr hwnd) => NativeMethods.IsWindowEnabled(hwnd);

    /// <summary>只移动不改变大小；用于几何变化后把窗口挪回虚拟屏。</summary>
    public static bool MoveTo(IntPtr hwnd, int x, int y)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SwpNoSize | NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate | NativeMethods.SwpShowWindow);
    }

    public static bool WaitForZoomed(IntPtr hwnd, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (NativeMethods.IsZoomed(hwnd))
            {
                return true;
            }

            Thread.Sleep(20);
        }

        return NativeMethods.IsZoomed(hwnd);
    }

    public static string GetProcessPath(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.MainModule?.FileName ?? string.Empty;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return string.Empty;
        }
    }
}
