using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using VScreenPilot.Platform.Interop;

namespace VScreenPilot.Platform.Displays;

/// <summary>VDD 的 IOCTL 与设备枚举细节；从 VddDevice 拆出。</summary>
internal sealed class VddIo
{
    private readonly VddDevice _host;

    public VddIo(VddDevice host) => _host = host;

    internal bool IoControl(uint code, ReadOnlySpan<byte> input, bool hasOutput, int timeoutMs, out int output)
    {
        // 驱动对并发 IOCTL 的容忍度有限：ADD/REMOVE 与心跳必须串行化。
        // 心跳线程在 ADD/REMOVE 期间会短暂等待；此时没有已挂载的屏需要保活。
        lock (_host._ioGate)
        {
            return IoControlCore(code, input, hasOutput, timeoutMs, out output);
        }
    }

    internal bool IoControlCore(uint code, ReadOnlySpan<byte> input, bool hasOutput, int timeoutMs, out int output)
    {
        output = -1;

        IntPtr handle;
        lock (_host._gate)
        {
            handle = _host._handle;
            if (handle == IntPtr.Zero || handle == Win32.InvalidHandleValue)
            {
                return false;
            }
        }

        IntPtr inputBuffer = Marshal.AllocHGlobal(32);
        IntPtr outputBuffer = Marshal.AllocHGlobal(4);
        var overlapped = new Win32.OVERLAPPED();

        try
        {
            for (int i = 0; i < 32; i++)
            {
                Marshal.WriteByte(inputBuffer, i, 0);
            }

            for (int i = 0; i < input.Length && i < 32; i++)
            {
                Marshal.WriteByte(inputBuffer, i, input[i]);
            }

            overlapped.hEvent = Win32.CreateEventW(IntPtr.Zero, false, false, null);
            if (overlapped.hEvent == IntPtr.Zero)
            {
                return false;
            }

            // 按参考实现：忽略 DeviceIoControl 的同步返回值，统一等待 OVERLAPPED。
            Win32.DeviceIoControl(
                handle, code,
                inputBuffer, 32,
                outputBuffer, hasOutput ? 4 : 0,
                IntPtr.Zero, ref overlapped);

            bool success = Win32.GetOverlappedResultEx(
                handle, ref overlapped, out _, (uint)timeoutMs, false);

            if (!success)
            {
                _host.LastError = Marshal.GetLastWin32Error();
                // 超时/错误时内核可能仍在写这块内存，必须取消后等它真正完成。
                // 注意：这里不能改成“超时就直接返回/释放”——OVERLAPPED 与两个缓冲区
                // 都在当前栈帧上，提前返回会让驱动写已释放/已复用的内存。
                // 真要给等待设上限，必须把 OVERLAPPED 移到非托管堆并交给后台线程收尾。
                Win32.CancelIoEx(handle, ref overlapped);
                Win32.GetOverlappedResult(handle, ref overlapped, out _, true);
                return false;
            }

            if (hasOutput)
            {
                output = Marshal.ReadInt32(outputBuffer);
            }

            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (overlapped.hEvent != IntPtr.Zero)
            {
                Win32.CloseHandle(overlapped.hEvent);
            }

            Marshal.FreeHGlobal(inputBuffer);
            Marshal.FreeHGlobal(outputBuffer);
        }
    }

    internal static bool MatchesHardwareId(IntPtr deviceInfoSet, ref Win32.SP_DEVINFO_DATA devInfo)
    {
        var buffer = new byte[1024];
        if (!Win32.SetupDiGetDeviceRegistryPropertyW(
                deviceInfoSet, ref devInfo, Win32.SpdrpHardwareId, out _,
                buffer, (uint)buffer.Length, out uint required) || required == 0)
        {
            return false;
        }

        int length = (int)Math.Min(required, (uint)buffer.Length);
        string value = Encoding.Unicode.GetString(buffer, 0, length);
        foreach (string part in value.Split('\0'))
        {
            if (part.Trim().Equals(VddDevice.HardwareId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
