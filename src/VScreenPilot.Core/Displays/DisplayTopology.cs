using System.Runtime.InteropServices;
using VScreenPilot.Core.Interop;

namespace VScreenPilot.Core.Displays;

public sealed record DisplayInfo(
    string DeviceName,
    string DeviceString,
    string DeviceId,
    bool Attached,
    bool Primary,
    int X,
    int Y,
    int Width,
    int Height,
    int Frequency,
    uint Dpi)
{
    public bool IsParsec =>
        DeviceString.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
        DeviceId.Contains(VddDevice.DisplayId, StringComparison.OrdinalIgnoreCase);

    public override string ToString() =>
        $"{DeviceName} \"{DeviceString}\" {(Attached ? "attached" : "detached")}" +
        $"{(Primary ? " primary" : string.Empty)} {Width}x{Height}@{Frequency} at ({X},{Y}) dpi={Dpi}";
}

public static class DisplayTopology
{
    public static IReadOnlyList<DisplayInfo> List()
    {
        var result = new List<DisplayInfo>();

        for (uint index = 0; ; index++)
        {
            var adapter = NewDisplayDevice();
            if (!Win32.EnumDisplayDevicesW(null, index, ref adapter, 0))
            {
                break;
            }

            if ((adapter.StateFlags & Win32.DisplayDeviceMirroringDriver) != 0)
            {
                continue;
            }

            var mode = NewDevMode();
            bool hasMode = Win32.EnumDisplaySettingsW(adapter.DeviceName, Win32.EnumCurrentSettings, ref mode);
            bool attached = (adapter.StateFlags & Win32.DisplayDeviceAttachedToDesktop) != 0;
            bool isParsec = adapter.DeviceString.Contains("Parsec", StringComparison.OrdinalIgnoreCase) ||
                            adapter.DeviceID.Contains(VddDevice.DisplayId, StringComparison.OrdinalIgnoreCase);

            // 未激活的 Intel 幻影适配器没有意义；Parsec 适配器即使未激活也要保留，
            // 因为需要拿它的 DeviceName 去 ChangeDisplaySettingsEx 激活。
            if (!hasMode && !attached && !isParsec)
            {
                continue;
            }

            result.Add(new DisplayInfo(
                adapter.DeviceName,
                adapter.DeviceString,
                adapter.DeviceID,
                attached,
                (adapter.StateFlags & Win32.DisplayDevicePrimaryDevice) != 0,
                hasMode ? mode.dmPositionX : 0,
                hasMode ? mode.dmPositionY : 0,
                hasMode ? mode.dmPelsWidth : 0,
                hasMode ? mode.dmPelsHeight : 0,
                hasMode ? mode.dmDisplayFrequency : 0,
                hasMode ? GetDpi(mode.dmPositionX + mode.dmPelsWidth / 2, mode.dmPositionY + mode.dmPelsHeight / 2) : 0));
        }

        return result;
    }

    public static DisplayInfo? FindParsecDisplay()
    {
        return List().FirstOrDefault(d => d.IsParsec && d.Attached);
    }

    public static DisplayInfo? FindParsecAdapter()
    {
        return List().FirstOrDefault(d => d.IsParsec);
    }

    public static bool WaitForParsecAdapter(TimeSpan timeout, out DisplayInfo? display)
    {
        DisplayInfo? found = null;

        // 仅枚举到未激活适配器还不够：新加的显示设备需要先具备模式（或已挂载）才能设模式。
        bool ok = WaitUntil(
            () =>
            {
                found = FindParsecAdapter();
                return found is not null && (found.Attached || found.Width > 0);
            },
            timeout);

        display = found;
        return ok;
    }

    /// <summary>调用 SetDisplayConfig 扩展桌面，激活新挂载的虚拟显示器。</summary>
    public static int ExtendDesktop()
    {
        return Win32.SetDisplayConfig(
            0, IntPtr.Zero, 0, IntPtr.Zero,
            Win32.SdcTopologyExtend | Win32.SdcApply);
    }

    /// <summary>扩展桌面但不让系统优化现有拓扑（尝试避免主屏重排/黑屏）。</summary>
    public static int ExtendDesktopNoOptimization()
    {
        return Win32.SetDisplayConfig(
            0, IntPtr.Zero, 0, IntPtr.Zero,
            Win32.SdcTopologyExtend | Win32.SdcApply |
            Win32.SdcNoOptimization | Win32.SdcAllowChanges);
    }

    public static DisplayInfo? FindPrimary()
    {
        return List().FirstOrDefault(d => d.Primary && d.Attached)
               ?? List().FirstOrDefault(d => d.Attached);
    }

    public static bool WaitForParsecDisplay(TimeSpan timeout, out DisplayInfo? display)
    {
        DisplayInfo? found = null;
        bool ok = WaitUntil(() => (found = FindParsecDisplay()) is not null, timeout);
        display = found;
        return ok;
    }

    public static bool WaitForNoParsecDisplay(TimeSpan timeout)
    {
        return WaitUntil(() => FindParsecDisplay() is null, timeout);
    }

    public static bool WaitUntil(Func<bool> predicate, TimeSpan timeout, int pollMs = 50)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(pollMs);
        }

        return predicate();
    }

    /// <summary>
    /// 设置指定显示器的分辨率、刷新率与虚拟桌面坐标。
    /// 返回 DISP_CHANGE 错误码，0 表示成功。
    /// </summary>
    public static int SetModeAndPosition(
        string deviceName,
        int width,
        int height,
        int frequency,
        int x,
        int y,
        out int applyResult)
    {
        var mode = NewDevMode();
        mode.dmFields = Win32.DmPelsWidth | Win32.DmPelsHeight | Win32.DmPosition |
                        Win32.DmDisplayFrequency | Win32.DmBitsPerPel;
        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmDisplayFrequency = frequency;
        mode.dmBitsPerPel = 32;
        mode.dmPositionX = x;
        mode.dmPositionY = y;

        int result = Win32.ChangeDisplaySettingsExW(
            deviceName, ref mode, IntPtr.Zero,
            Win32.CdsUpdateregistry | Win32.CdsNoReset, IntPtr.Zero);

        if (result != Win32.DispChangeSuccessful && result != Win32.DispChangeRestart)
        {
            applyResult = result;
            return result;
        }

        applyResult = Win32.ChangeDisplaySettingsExW(
            null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

        // 注意：部分 VDD/Windows 组合会忽略新激活显示器的 dmPosition，
        // 固定放在主屏右侧。这里不再做第二次模式切换（会扰动驱动）；
        // 位置未生效由 SessionManager 记录 position_enforced=false 并接受实际坐标。
        return applyResult;
    }

    /// <summary>
    /// 只对目标显示器调用 ChangeDisplaySettingsEx，不做全局 apply。
    /// 用于验证是否避免主屏黑屏。
    /// </summary>
    public static int SetModeAndPositionDeviceOnly(
        string deviceName, int width, int height, int frequency, int x, int y)
    {
        var mode = NewDevMode();
        mode.dmFields = Win32.DmPelsWidth | Win32.DmPelsHeight | Win32.DmPosition |
                        Win32.DmDisplayFrequency | Win32.DmBitsPerPel;
        mode.dmPelsWidth = width;
        mode.dmPelsHeight = height;
        mode.dmDisplayFrequency = frequency;
        mode.dmBitsPerPel = 32;
        mode.dmPositionX = x;
        mode.dmPositionY = y;

        return Win32.ChangeDisplaySettingsExW(
            deviceName, ref mode, IntPtr.Zero, Win32.CdsUpdateregistry, IntPtr.Zero);
    }

    public static (int X, int Y, int Width, int Height) VirtualScreenBounds()
    {
        return (
            Win32.GetSystemMetrics(Win32.SmXVirtualScreen),
            Win32.GetSystemMetrics(Win32.SmYVirtualScreen),
            Win32.GetSystemMetrics(Win32.SmCxVirtualScreen),
            Win32.GetSystemMetrics(Win32.SmCyVirtualScreen));
    }

    private static uint GetDpi(int x, int y)
    {
        try
        {
            IntPtr monitor = Win32.MonitorFromPoint(
                new Win32.POINT { X = x, Y = y }, Win32.MonitorDefaultToNearest);

            if (monitor != IntPtr.Zero &&
                Win32.GetDpiForMonitor(monitor, Win32.MdtEffectiveDpi, out uint dpiX, out _) == 0)
            {
                return dpiX;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 0;
    }

    private static Win32.DISPLAY_DEVICE NewDisplayDevice()
    {
        return new Win32.DISPLAY_DEVICE
        {
            cb = Marshal.SizeOf<Win32.DISPLAY_DEVICE>(),
            DeviceName = string.Empty,
            DeviceString = string.Empty,
            DeviceID = string.Empty,
            DeviceKey = string.Empty,
        };
    }

    private static Win32.DEVMODE NewDevMode()
    {
        return new Win32.DEVMODE
        {
            dmSize = (short)Marshal.SizeOf<Win32.DEVMODE>(),
            dmDeviceName = string.Empty,
            dmFormName = string.Empty,
        };
    }
}
