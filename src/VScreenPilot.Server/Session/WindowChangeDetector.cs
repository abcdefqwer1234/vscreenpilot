using VScreenPilot.Platform.Windows;

namespace VScreenPilot.Session;

/// <summary>多击期间新出现的顶层窗口；用于在第二击落进新弹窗前中止。</summary>
internal sealed record WindowChange(IntPtr Hwnd, string Title, string ClassName);

/// <summary>
/// 顶层窗口集合快照与差异检测。只认可见、有面积、非菜单（#32768）的窗口，
/// 避免把输入法候选框/工具提示误判成“新对话框”。
/// </summary>
internal static class WindowChangeDetector
{
    public static HashSet<IntPtr> Capture(int pid)
    {
        var result = new HashSet<IntPtr>();

        foreach (WindowInfo window in WindowManager.ListForProcess(pid))
        {
            if (IsCandidate(window))
            {
                result.Add(window.Hwnd);
            }
        }

        return result;
    }

    /// <summary>返回 before 之后新出现的第一个候选顶层窗口；没有则 null。</summary>
    public static WindowChange? FindNew(int pid, IReadOnlySet<IntPtr> before) =>
        FindNew(
            before,
            WindowManager.ListForProcess(pid).Select(window => (
                window.Hwnd, window.Title, window.ClassName, window.Visible, window.Area)));

    /// <summary>纯函数版本，供单测使用。</summary>
    public static WindowChange? FindNew(
        IReadOnlySet<IntPtr> before,
        IEnumerable<(IntPtr Hwnd, string Title, string ClassName, bool Visible, long Area)> windows)
    {
        foreach ((IntPtr hwnd, string title, string className, bool visible, long area) in windows)
        {
            if (!before.Contains(hwnd) &&
                visible &&
                area > 0 &&
                !IsTransientPopupClass(className))
            {
                return new WindowChange(hwnd, title, className);
            }
        }

        return null;
    }

    private static bool IsCandidate(WindowInfo window) =>
        window.Visible &&
        window.Area > 0 &&
        !IsTransientPopupClass(window.ClassName);

    /// <summary>菜单、tooltip、IME 候选框不是“应用打开的对话框”，不能中止多击。</summary>
    private static bool IsTransientPopupClass(string? className) =>
        className is not null &&
        (string.Equals(className, "#32768", StringComparison.Ordinal) ||
         string.Equals(className, "tooltips_class32", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(className, "IME", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(className, "MSCTFIME UI", StringComparison.OrdinalIgnoreCase));
}
