using System.Text;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;

namespace VScreenPilot.Input;

/// <summary>
/// 不移动系统光标的鼠标注入：向目标窗口投递窗口消息。
/// 默认路径；Electron/Chromium 等可能忽略，需由调用方验证。
/// </summary>
internal static class MessageInput
{
    private const uint WmMouseMove = 0x0200;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmMButtonDown = 0x0207;
    private const uint WmMButtonUp = 0x0208;
    private const uint WmMouseWheel = 0x020A;
    private const uint WmMouseHWheel = 0x020E;

    private const int MkLButton = 0x0001;
    private const int MkRButton = 0x0002;
    private const int MkMButton = 0x0010;

    public static bool Click(int x, int y, string button, int clicks, out string detail)
    {
        detail = string.Empty;

        IntPtr target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (target == IntPtr.Zero)
        {
            detail = "WindowFromPoint 找不到目标窗口";
            return false;
        }

        if (IsInputHostile(target))
        {
            detail = "目标属于 Chromium/Electron/WinUI，窗口消息点击会被忽略；请使用 click mode=input 或 CDP";
            return false;
        }

        NativeMethods.POINT client = ToClient(target, x, y);

        (uint down, uint up, int keyState) = ButtonMessages(button);
        IntPtr position = MakeLParam(client.X, client.Y);

        bool ok = NativeMethods.PostMessageW(target, WmMouseMove, IntPtr.Zero, position);
        int count = Math.Max(1, Math.Min(clicks, 10));
        bool isLeft = button.Equals("left", StringComparison.OrdinalIgnoreCase);

        for (int i = 0; i < count; i++)
        {
            // Windows 的多击序列：第一次 DOWN/UP，后续每次用 DBLCLK/UP 代替 DOWN/UP。
            uint downMessage = i == 0 || !isLeft ? down : WmLButtonDblClk;
            ok &= NativeMethods.PostMessageW(target, downMessage, (IntPtr)keyState, position);
            ok &= NativeMethods.PostMessageW(target, up, IntPtr.Zero, position);
        }

        detail = $"hwnd=0x{target.ToInt64():X}";
        return ok;
    }

    public static bool Drag(
        int fromX, int fromY, int toX, int toY, string button, int durationMs, out string detail)
    {
        detail = string.Empty;

        IntPtr target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = fromX, Y = fromY });
        if (target == IntPtr.Zero)
        {
            detail = "WindowFromPoint 找不到目标窗口";
            return false;
        }

        // 窗口消息拖拽无法在目标进程里 SetCapture，跨子窗口/拖放会“成功但没效果”。
        // 与其返回假成功，不如明确要求调用方选 input。
        detail = "窗口消息拖拽无法维持鼠标捕获；请使用 gesture mode=input";
        return false;
    }

    public static bool Scroll(int x, int y, int dx, int dy, out string detail)
    {
        detail = string.Empty;

        IntPtr target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (target == IntPtr.Zero)
        {
            detail = "WindowFromPoint 找不到目标窗口";
            return false;
        }

        if (IsInputHostile(target))
        {
            detail = "目标属于 Chromium/Electron/WinUI，窗口消息滚轮会被忽略；请使用 gesture mode=input";
            return false;
        }

        // 滚轮通常由顶层窗口/焦点窗口处理；只发给子窗口在部分应用里会被忽略。
        // 同时按“实际接收消息的窗口”做 DPI 转换，避免高 DPI 下坐标偏移（R19）。
        IntPtr wheelTarget = NativeMethods.GetAncestor(target, NativeMethods.GaRoot);
        if (wheelTarget == IntPtr.Zero)
        {
            wheelTarget = target;
        }

        var wheelPoint = new NativeMethods.POINT { X = x, Y = y };
        NativeMethods.PhysicalToLogicalPointForPerMonitorDPI(wheelTarget, ref wheelPoint);
        IntPtr lParam = MakeLParam(wheelPoint.X, wheelPoint.Y);
        bool ok = true;

        if (dy != 0)
        {
            int delta = -dy * 120;
            ok &= NativeMethods.PostMessageW(
                wheelTarget, WmMouseWheel, (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000)), lParam);
        }

        if (dx != 0)
        {
            int delta = dx * 120;
            ok &= NativeMethods.PostMessageW(
                wheelTarget, WmMouseHWheel, (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000)), lParam);
        }

        detail = $"hwnd=0x{wheelTarget.ToInt64():X} child=0x{target.ToInt64():X}";
        return ok;
    }

    public static bool Move(int x, int y, out string detail)
    {
        detail = string.Empty;

        IntPtr target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (target == IntPtr.Zero)
        {
            detail = "WindowFromPoint 找不到目标窗口";
            return false;
        }

        if (IsInputHostile(target))
        {
            detail = "目标属于 Chromium/Electron/WinUI，后台移动不会产生鼠标事件；请使用 gesture mode=input";
            return false;
        }

        NativeMethods.POINT client = ToClient(target, x, y);
        detail = $"hwnd=0x{target.ToInt64():X}";
        return NativeMethods.PostMessageW(target, WmMouseMove, IntPtr.Zero, MakeLParam(client.X, client.Y));
    }

    /// <summary>
    /// Chromium/Electron/WinUI 的输入管线不消费 PostMessage；对这类窗口直接拒绝消息注入，
    /// 让调用方显式选择 SendInput 或 CDP，而不是返回一个“成功但没效果”的结果。
    /// </summary>
    internal static bool IsInputHostile(IntPtr hwnd)
    {
        IntPtr current = hwnd;

        for (int depth = 0; depth < 6 && current != IntPtr.Zero; depth++)
        {
            var builder = new StringBuilder(256);
            if (NativeMethods.GetClassNameW(current, builder, builder.Capacity) > 0)
            {
                string name = builder.ToString();

                if (name.Contains("Chrome_WidgetWin", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Chrome_RenderWidgetHost", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("CefSharp", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Electron", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            current = NativeMethods.GetAncestor(current, 1); // GA_PARENT
        }

        return false;
    }

    private static (uint Down, uint Up, int KeyState) ButtonMessages(string button) =>
        button.ToLowerInvariant() switch
        {
            "left" => (WmLButtonDown, WmLButtonUp, MkLButton),
            "right" => (WmRButtonDown, WmRButtonUp, MkRButton),
            "middle" => (WmMButtonDown, WmMButtonUp, MkMButton),
            _ => throw new ToolException("E_INVALID_ARG", $"未知鼠标按键: {button}"),
        };

    /// <summary>
    /// 物理屏幕坐标 → 目标窗口客户区坐标。DPI-unaware 目标需要先做 Per-Monitor DPI 转换，
    /// 否则在 150% 缩放下 PostMessage 的落点会偏。
    /// </summary>
    private static NativeMethods.POINT ToClient(IntPtr hwnd, int x, int y)
    {
        var point = new NativeMethods.POINT { X = x, Y = y };
        NativeMethods.PhysicalToLogicalPointForPerMonitorDPI(hwnd, ref point);

        if (!NativeMethods.ScreenToClient(hwnd, ref point))
        {
            point.X = x;
            point.Y = y;
        }

        return point;
    }

    private static IntPtr MakeLParam(int x, int y) =>
        (IntPtr)((y << 16) | (x & 0xFFFF));
}
