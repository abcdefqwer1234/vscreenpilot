using VScreenPilot.Interop;

namespace VScreenPilot.Server.Infrastructure;

/// <summary>进程/桌面/显示器探测的单一实现，避免各组件各自复制。</summary>
internal static class EnvironmentProbe
{
    public static string ProcessStartTime()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.StartTime.ToString("O");
        }
        catch
        {
            return DateTimeOffset.Now.ToString("O");
        }
    }

    public static bool IsInputDesktopAvailable()
    {
        IntPtr desktop = NativeMethods.OpenInputDesktop(0, false, 0x0100);
        if (desktop == IntPtr.Zero)
        {
            return false;
        }

        NativeMethods.CloseDesktop(desktop);
        return true;
    }

    public static int GetMonitorCount() => NativeMethods.GetSystemMetrics(80);
}
