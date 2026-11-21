using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VScreenPilot.Platform.Processes;

/// <summary>
/// 作业对象：父进程退出/崩溃时由操作系统回收所有已加入的子进程。
/// 注意：AssignProcessToJobObject 在“父进程已在某个不允许 breakaway 的 job 中”时会失败，
/// 因此这里把结果如实暴露给调用方，而不是假装兜底一定可用。
/// </summary>
public sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _handle;
    private int _disposed;
    private string _status = "unavailable";
    private int _lastError;

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformationStruct
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, IntPtr lpJobObjectInformation, uint cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(IntPtr processHandle, IntPtr jobHandle, out bool result);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    public JobObject()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);

        if (_handle == IntPtr.Zero)
        {
            _lastError = Marshal.GetLastWin32Error();
            _status = $"create_failed:{_lastError}";
            return;
        }

        var info = new JobObjectExtendedLimitInformationStruct();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        int size = Marshal.SizeOf<JobObjectExtendedLimitInformationStruct>();
        IntPtr buffer = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(info, buffer, false);

            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                _lastError = Marshal.GetLastWin32Error();
                // 句柄仍可用，只是崩溃兜底不保证；如实记录，不阻断启动。
                _status = $"limits_failed:{_lastError}";
                return;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        _status = "ready";
    }

    public bool IsValid => _handle != IntPtr.Zero && Volatile.Read(ref _disposed) == 0;

    /// <summary>ready / assigned / assign_failed:5 / create_failed:5 / limits_failed:5。</summary>
    public string Status => Volatile.Read(ref _status);

    public int LastError => Volatile.Read(ref _lastError);

    public bool Assign(System.Diagnostics.Process process)
    {
        if (!IsValid)
        {
            return false;
        }

        try
        {
            if (process.HasExited)
            {
                _status = "assign_skipped_exited";
                return false;
            }

            if (!AssignProcessToJobObject(_handle, process.Handle))
            {
                _lastError = Marshal.GetLastWin32Error();
                _status = $"assign_failed:{_lastError}";
                return false;
            }

            if (IsProcessInJob(process.Handle, _handle, out bool inJob) && !inJob)
            {
                _status = "assign_unverified";
                return false;
            }

            _status = "assigned";
            return true;
        }
        catch (Exception exception)
        {
            _status = $"assign_exception:{exception.GetType().Name}";
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        IntPtr handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);

        if (handle != IntPtr.Zero)
        {
            CloseHandle(handle);
        }
    }
}
