using System.Diagnostics;
using System.Text;
using VScreenPilot.Interop;

namespace VScreenPilot.Apps;

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

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                string title = GetTitle(hwnd);

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
                NativeMethods.GetWindowRect(hwnd, out var rect);

                var className = new StringBuilder(256);
                NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

                bool owned = NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero;

                result.Add(new WindowInfo(
                    hwnd, (int)pid, title, className.ToString(), rect,
                    Visible: true, Owned: owned));
            }
            catch
            {
                // 枚举过程中的个别窗口失败不影响整体。
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    public static WindowInfo? FindByTitle(
        string titleSubstring, bool exact = false, Func<WindowInfo, bool>? filter = null)
    {
        var candidates = FindByTitleCandidates(titleSubstring, filter);

        if (exact)
        {
            return candidates.FirstOrDefault(
                item => item.Title.Equals(titleSubstring, StringComparison.OrdinalIgnoreCase));
        }

        return candidates
            .OrderByDescending(item => item.Title.Equals(titleSubstring, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Area)
            .FirstOrDefault();
    }

    /// <summary>
    /// 返回所有标题匹配的顶层窗口，按“完全相等优先、面积降序”排序。
    /// 调用方在多候选时应报歧义，而不是随手接管第一个。
    /// </summary>
    public static IReadOnlyList<WindowInfo> FindByTitleCandidates(
        string titleSubstring, Func<WindowInfo, bool>? filter = null)
    {
        var result = new List<WindowInfo>();

        NativeMethods.EnumWindows((hwnd, _) =>
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
            catch
            {
                // 单个窗口枚举失败不影响其他候选。
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

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!NativeMethods.IsWindowVisible(hwnd))
                {
                    return true;
                }

                NativeMethods.GetWindowThreadProcessId(hwnd, out uint ownerPid);
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
            catch
            {
                // 枚举中的个别窗口失败不影响整体。
            }

            return true;
        }, IntPtr.Zero);

        return bestWithTitle ?? bestUnowned ?? bestAny;
    }

    private static WindowInfo Describe(IntPtr hwnd, string title)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

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

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        NativeMethods.GetWindowRect(hwnd, out var rect);
        var className = new StringBuilder(256);
        NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

        return new WindowInfo(
            hwnd, (int)pid, GetTitle(hwnd), className.ToString(), rect,
            NativeMethods.IsWindowVisible(hwnd),
            NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero);
    }

    public static string GetTitle(IntPtr hwnd)
    {
        int length = NativeMethods.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var buffer = new StringBuilder(length + 1);
        NativeMethods.GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    public static IntPtr GetForegroundWindow() => NativeMethods.GetForegroundWindow();

    public static int GetProcessId(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }

    public static bool IsForegroundOfProcess(int pid) =>
        GetProcessId(NativeMethods.GetForegroundWindow()) == pid;

    public static IReadOnlyList<WindowInfo> ListForProcess(int pid) =>
        ListTopLevel().Where(window => window.Pid == pid).ToList();

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
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                attachedForeground = NativeMethods.AttachThreadInput(currentThread, foregroundThread, true);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                attachedTarget = NativeMethods.AttachThreadInput(currentThread, targetThread, true);
            }

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
        // 不主动 SetForegroundWindow：启动/归位不抢用户焦点，输入时再短暂切换并恢复。
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
        catch
        {
            return string.Empty;
        }
    }
}
