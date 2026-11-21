using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VScreenPilot.Platform.Ipc;

/// <summary>
/// 命名管道客户端完整性校验。高完整性 daemon 不能被普通进程借用，
/// 反向不一致也拒绝，避免客户端误以为能操作 elevated 窗口。
/// </summary>
internal static class IntegrityCheck
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientPid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr token, int informationClass, IntPtr information, uint length, out uint returnLength);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static bool TryCheck(SafePipeHandle pipe, out string reason)
    {
        reason = string.Empty;

        if (string.Equals(
                Environment.GetEnvironmentVariable("VSP_ALLOW_INTEGRITY_MISMATCH"), "1",
                StringComparison.Ordinal))
        {
            return true;
        }

        if (!GetNamedPipeClientProcessId(pipe, out uint clientPid))
        {
            reason = "无法获取管道客户端 pid";
            return false;
        }

        int clientIntegrity = TryGetIntegrity(clientPid, out int value) ? value : -1;
        int daemonIntegrity = TryGetIntegrity((uint)Environment.ProcessId, out int own) ? own : -1;

        if (clientIntegrity < 0 || daemonIntegrity < 0)
        {
            // 查询失败按“无法证明可信”处理；测试/排障可用
            // VSP_ALLOW_INTEGRITY_MISMATCH=1 显式放行（见 SECURITY.md）。
            reason = $"完整性查询失败 client={clientPid}";
            return false;
        }

        if (clientIntegrity != daemonIntegrity)
        {
            reason = $"完整性不一致 daemon={daemonIntegrity} client={clientIntegrity} pid={clientPid}";
            return false;
        }

        return true;
    }

    private static bool TryGetIntegrity(uint processId, out int integrity)
    {
        integrity = -1;
        IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (process == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out IntPtr token))
            {
                return false;
            }

            try
            {
                _ = GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out uint size);

                if (size == 0)
                {
                    return false;
                }

                IntPtr buffer = Marshal.AllocHGlobal((int)size);

                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, size, out _))
                    {
                        return false;
                    }

                    // TOKEN_MANDATORY_LABEL { SID_AND_ATTRIBUTES { PSID Sid; DWORD Attributes; } }
                    IntPtr sid = Marshal.ReadIntPtr(buffer);
                    IntPtr countPointer = GetSidSubAuthorityCount(sid);

                    if (countPointer == IntPtr.Zero)
                    {
                        return false;
                    }

                    byte count = Marshal.ReadByte(countPointer);

                    if (count == 0)
                    {
                        return false;
                    }

                    IntPtr subAuthority = GetSidSubAuthority(sid, (uint)(count - 1));
                    integrity = subAuthority == IntPtr.Zero ? -1 : Marshal.ReadInt32(subAuthority);
                    return integrity >= 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }
}
