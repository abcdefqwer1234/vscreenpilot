using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using VScreenPilot.Platform.Interop;

namespace VScreenPilot.Platform.Displays;

public enum VddDeviceStatus
{
    Ok = 0,
    Inaccessible = 1,
    Unknown = 2,
    UnknownProblem = 3,
    Disabled = 4,
    DriverError = 5,
    RestartRequired = 6,
    DisabledService = 7,
    NotInstalled = 8,
}

/// <summary>
/// Parsec VDD 的设备句柄与 IOCTL。一个进程内应只创建一个实例，
/// 并在整个会话期间维持 100ms 心跳，否则驱动会在约 1 秒后自动拔掉所有虚拟屏。
/// </summary>
public sealed class VddDevice : IDisposable
{
    public static readonly Guid AdapterInterfaceGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");
    public static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");
    public const string HardwareId = @"Root\Parsec\VDA";
    public const string DisplayId = "PSCCDD0";
    public const string DisplayFriendlyName = "ParsecVDA";

    private const uint IoctlAdd = 0x0022E004;
    private const uint IoctlRemove = 0x0022A008;
    private const uint IoctlUpdate = 0x0022A00C;
    private const uint IoctlVersion = 0x0022E010;

    private readonly object _gate = new();
    private readonly object _ioGate = new();
    private IntPtr _handle = IntPtr.Zero;
    private Thread? _keepAliveThread;
    private volatile bool _keepAliveRunning;
    private long _pingCount;
    private int _consecutiveUpdateFailures;

    public bool IsOpen
    {
        get
        {
            lock (_gate)
            {
                return _handle != IntPtr.Zero && _handle != Win32.InvalidHandleValue;
            }
        }
    }

    public long PingCount => Interlocked.Read(ref _pingCount);

    /// <summary>最近一次 IOCTL 失败的 Win32 错误码，仅用于诊断。</summary>
    public int LastError { get; private set; }

    /// <summary>打开设备接口。返回 false 时 err 为 Win32 错误码（5 = 拒绝访问）。</summary>
    public bool TryOpen(out int error)
    {
        error = 0;
        lock (_gate)
        {
            if (IsOpen)
            {
                return true;
            }

            Guid adapterGuid = AdapterInterfaceGuid;
            IntPtr deviceInfoSet = Win32.SetupDiGetClassDevsW(
                ref adapterGuid, null, IntPtr.Zero,
                Win32.DigcfPresent | Win32.DigcfDeviceInterface);

            if (deviceInfoSet == Win32.InvalidHandleValue)
            {
                error = Marshal.GetLastWin32Error();
                return false;
            }

            try
            {
                var interfaceData = new Win32.SP_DEVICE_INTERFACE_DATA
                {
                    cbSize = Marshal.SizeOf<Win32.SP_DEVICE_INTERFACE_DATA>(),
                };

                for (uint index = 0;
                     Win32.SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero,
                         ref adapterGuid, index, ref interfaceData);
                     index++)
                {
                    if (!Win32.SetupDiGetDeviceInterfaceDetailW(
                            deviceInfoSet, ref interfaceData, IntPtr.Zero, 0,
                            out uint requiredSize, IntPtr.Zero))
                    {
                        // 首次调用按约定返回 false + ERROR_INSUFFICIENT_BUFFER，
                        // requiredSize 有效时继续。
                        error = Marshal.GetLastWin32Error();
                        if (requiredSize == 0)
                        {
                            continue;
                        }
                    }

                    IntPtr detail = Marshal.AllocHGlobal((int)requiredSize);
                    try
                    {
                        // x64 固定为 8；DevicePath 仍从偏移 4 开始。
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!Win32.SetupDiGetDeviceInterfaceDetailW(
                                deviceInfoSet, ref interfaceData, detail, requiredSize,
                                out _, IntPtr.Zero))
                        {
                            error = Marshal.GetLastWin32Error();
                            continue;
                        }

                        string? path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (string.IsNullOrWhiteSpace(path))
                        {
                            continue;
                        }

                        IntPtr handle = Win32.CreateFileW(
                            path,
                            Win32.GenericRead | Win32.GenericWrite,
                            Win32.FileShareRead | Win32.FileShareWrite,
                            IntPtr.Zero,
                            Win32.OpenExisting,
                            Win32.FileAttributeNormal | Win32.FileFlagOverlapped |
                            Win32.FileFlagNoBuffering | Win32.FileFlagWriteThrough,
                            IntPtr.Zero);

                        if (handle != IntPtr.Zero && handle != Win32.InvalidHandleValue)
                        {
                            _handle = handle;
                            Interlocked.Exchange(ref _consecutiveUpdateFailures, 0);
                            error = 0;
                            return true;
                        }

                        error = Marshal.GetLastWin32Error();
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                Win32.SetupDiDestroyDeviceInfoList(deviceInfoSet);
            }

            if (error == 0)
            {
                error = Marshal.GetLastWin32Error();
            }

            return false;
        }
    }

    public static VddDeviceStatus QueryStatus()
    {
        Guid classGuid = DisplayClassGuid;
        IntPtr deviceInfoSet = Win32.SetupDiGetClassDevsW(
            ref classGuid, null, IntPtr.Zero, Win32.DigcfPresent);

        if (deviceInfoSet == Win32.InvalidHandleValue)
        {
            return VddDeviceStatus.NotInstalled;
        }

        try
        {
            var devInfo = new Win32.SP_DEVINFO_DATA
            {
                cbSize = Marshal.SizeOf<Win32.SP_DEVINFO_DATA>(),
            };

            for (uint index = 0; Win32.SetupDiEnumDeviceInfo(deviceInfoSet, index, ref devInfo); index++)
            {
                if (!MatchesHardwareId(deviceInfoSet, ref devInfo))
                {
                    continue;
                }

                if (Win32.CM_Get_DevNode_Status(out uint status, out uint problem, devInfo.DevInst, 0) != 0)
                {
                    return VddDeviceStatus.NotInstalled;
                }

                if ((status & (Win32.DnDriverLoaded | Win32.DnStarted)) ==
                    (Win32.DnDriverLoaded | Win32.DnStarted))
                {
                    return VddDeviceStatus.Ok;
                }

                if ((status & Win32.DnHasProblem) == 0)
                {
                    return VddDeviceStatus.Unknown;
                }

                return problem switch
                {
                    Win32.CmProbNeedRestart => VddDeviceStatus.RestartRequired,
                    Win32.CmProbDisabled or Win32.CmProbHardwareDisabled => VddDeviceStatus.Disabled,
                    Win32.CmProbDisabledService => VddDeviceStatus.DisabledService,
                    Win32.CmProbFailedPostStart => VddDeviceStatus.DriverError,
                    _ => VddDeviceStatus.UnknownProblem,
                };
            }
        }
        finally
        {
            Win32.SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }

        return VddDeviceStatus.NotInstalled;
    }

    private static bool MatchesHardwareId(IntPtr deviceInfoSet, ref Win32.SP_DEVINFO_DATA devInfo)
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
            if (part.Trim().Equals(HardwareId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public int GetVersion()
    {
        return IoControl(IoctlVersion, ReadOnlySpan<byte>.Empty, hasOutput: true, 1000, out int value)
            ? value
            : -1;
    }

    public int AddDisplay(int timeoutMs = 5000)
    {
        if (!IoControl(IoctlAdd, ReadOnlySpan<byte>.Empty, hasOutput: true, timeoutMs, out int index))
        {
            return -1;
        }

        Update();
        return index;
    }

    public bool RemoveDisplay(int index, int timeoutMs = 5000)
    {
        // 驱动 ABI：REMOVE 的输入是 16 位大端 index（见上游 parsec-vdd 的 VddRemoveDisplay）。
        // 若按小端 UINT32 写到 offset 0，index=1 会被驱动读成 0x0100(256)，返回 ERROR_INVALID_PARAMETER；
        // 只有 index=0 恰好正确，多屏/共享驱动场景会摘错屏或摘不掉。
        Span<byte> input = stackalloc byte[2];
        EncodeRemoveIndex(index, input);

        if (!IoControl(IoctlRemove, input, hasOutput: false, timeoutMs, out _))
        {
            return false;
        }

        Update();
        return true;
    }

    /// <summary>把 index 编码成驱动要求的 16 位大端序；抽出来便于单测。</summary>
    internal static void EncodeRemoveIndex(int index, Span<byte> destination)
    {
        destination.Clear();
        destination[0] = (byte)((index >> 8) & 0xFF);
        destination[1] = (byte)(index & 0xFF);
    }

    /// <summary>心跳。必须约每 100ms 调用一次。</summary>
    public void Update()
    {
        bool ok = IoControl(IoctlUpdate, ReadOnlySpan<byte>.Empty, hasOutput: false, 200, out _);

        if (ok)
        {
            Interlocked.Exchange(ref _consecutiveUpdateFailures, 0);
        }
        else
        {
            Interlocked.Increment(ref _consecutiveUpdateFailures);
        }
    }

    /// <summary>心跳连续失败次数；用于让会话层决定是否重新打开设备。</summary>
    public int ConsecutiveUpdateFailures => Volatile.Read(ref _consecutiveUpdateFailures);

    /// <summary>连续失败约 2 秒（20 次）时建议重开句柄。</summary>
    public bool NeedsReopen => ConsecutiveUpdateFailures >= 20;

    public void StartKeepAlive()
    {
        if (_keepAliveRunning)
        {
            return;
        }

        _keepAliveRunning = true;
        _keepAliveThread = new Thread(() =>
        {
            while (_keepAliveRunning)
            {
                if (IsOpen)
                {
                    Update();
                    Interlocked.Increment(ref _pingCount);
                }

                Thread.Sleep(100);
            }
        })
        {
            IsBackground = true,
            Name = "VddKeepAlive",
        };
        _keepAliveThread.Start();
    }

    public void StopKeepAlive()
    {
        _keepAliveRunning = false;
        _keepAliveThread?.Join(1000);
        _keepAliveThread = null;
    }

    private bool IoControl(uint code, ReadOnlySpan<byte> input, bool hasOutput, int timeoutMs, out int output)
    {
        // 驱动对并发 IOCTL 的容忍度有限：ADD/REMOVE 与心跳必须串行化。
        // 心跳线程在 ADD/REMOVE 期间会短暂等待；此时没有已挂载的屏需要保活。
        lock (_ioGate)
        {
            return IoControlCore(code, input, hasOutput, timeoutMs, out output);
        }
    }

    private bool IoControlCore(uint code, ReadOnlySpan<byte> input, bool hasOutput, int timeoutMs, out int output)
    {
        output = -1;

        IntPtr handle;
        lock (_gate)
        {
            handle = _handle;
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
                LastError = Marshal.GetLastWin32Error();
                // 超时/错误时内核可能仍在写这块内存，必须取消并阻塞等待完成。
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

    public void Dispose()
    {
        StopKeepAlive();

        lock (_gate)
        {
            if (_handle != IntPtr.Zero && _handle != Win32.InvalidHandleValue)
            {
                Win32.CloseHandle(_handle);
            }

            _handle = IntPtr.Zero;
        }

        Interlocked.Exchange(ref _consecutiveUpdateFailures, 0);
    }
}
