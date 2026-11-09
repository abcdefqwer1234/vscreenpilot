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

        var client = new NativeMethods.POINT { X = x, Y = y };
        if (!NativeMethods.ScreenToClient(target, ref client))
        {
            detail = "ScreenToClient 失败";
            return false;
        }

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

        (uint down, uint up, int keyState) = ButtonMessages(button);

        var start = new NativeMethods.POINT { X = fromX, Y = fromY };
        NativeMethods.ScreenToClient(target, ref start);

        bool ok = NativeMethods.PostMessageW(target, WmMouseMove, IntPtr.Zero, MakeLParam(start.X, start.Y));
        ok &= NativeMethods.PostMessageW(target, down, (IntPtr)keyState, MakeLParam(start.X, start.Y));

        int steps = Math.Clamp(durationMs / 10, 1, 100);
        for (int i = 1; i <= steps; i++)
        {
            int sx = fromX + (toX - fromX) * i / steps;
            int sy = fromY + (toY - fromY) * i / steps;
            var point = new NativeMethods.POINT { X = sx, Y = sy };
            NativeMethods.ScreenToClient(target, ref point);
            ok &= NativeMethods.PostMessageW(
                target, WmMouseMove, (IntPtr)keyState, MakeLParam(point.X, point.Y));
            Thread.Sleep(Math.Max(1, durationMs / steps));
        }

        var end = new NativeMethods.POINT { X = toX, Y = toY };
        NativeMethods.ScreenToClient(target, ref end);
        ok &= NativeMethods.PostMessageW(target, up, IntPtr.Zero, MakeLParam(end.X, end.Y));
        detail = $"hwnd=0x{target.ToInt64():X} steps={steps}";
        return ok;
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

        IntPtr lParam = MakeLParam(x, y);
        bool ok = true;

        if (dy != 0)
        {
            int delta = -dy * 120;
            ok &= NativeMethods.PostMessageW(
                target, WmMouseWheel, (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000)), lParam);
        }

        if (dx != 0)
        {
            int delta = dx * 120;
            ok &= NativeMethods.PostMessageW(
                target, WmMouseHWheel, (IntPtr)((delta << 16) & unchecked((int)0xFFFF0000)), lParam);
        }

        detail = $"hwnd=0x{target.ToInt64():X}";
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

        var client = new NativeMethods.POINT { X = x, Y = y };
        NativeMethods.ScreenToClient(target, ref client);
        detail = $"hwnd=0x{target.ToInt64():X}";
        return NativeMethods.PostMessageW(target, WmMouseMove, IntPtr.Zero, MakeLParam(client.X, client.Y));
    }

    private static (uint Down, uint Up, int KeyState) ButtonMessages(string button) =>
        button.ToLowerInvariant() switch
        {
            "left" => (WmLButtonDown, WmLButtonUp, MkLButton),
            "right" => (WmRButtonDown, WmRButtonUp, MkRButton),
            "middle" => (WmMButtonDown, WmMButtonUp, MkMButton),
            _ => throw new ToolException("E_INVALID_ARG", $"未知鼠标按键: {button}"),
        };

    private static IntPtr MakeLParam(int x, int y) =>
        (IntPtr)((y << 16) | (x & 0xFFFF));
}
