using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Ipc;

/// <summary>
/// 从首个 MCP 进程启动独立 daemon。优先用 CREATE_BREAKAWAY_FROM_JOB，
/// 避免 daemon 跟随 OpenCode 的 kill-on-close Job；不允许 breakaway 时回退普通启动。
/// </summary>
internal static class DaemonSpawner
{
    private const uint CreateBreakawayFromJob = 0x01000000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const int GenericReadWrite = unchecked((int)0x80000000) | 0x40000000;
    private const int FileShareReadWrite = 0x00000003;
    private const int OpenExisting = 3;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;

        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        int desiredAccess,
        int shareMode,
        ref SecurityAttributes securityAttributes,
        int creationDisposition,
        int flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static Process? TryStart(string profile, LogRing log)
    {
        try
        {
            string? exe = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(exe))
            {
                return null;
            }

            Process? breakaway = TryStartBreakaway(exe, profile, log);

            if (breakaway is not null)
            {
                return breakaway;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory,
            };

            startInfo.ArgumentList.Add("--daemon");
            startInfo.ArgumentList.Add("--profile");
            startInfo.ArgumentList.Add(profile);

            // 自动 spawn 的 daemon 不继承客户端的策略；能力通过每个 proxy 握手传递。
            foreach (string key in new[]
                     {
                         "VSP_ALLOW_SCRIPT",
                         "VSP_ALLOW_UNSAFE_SCRIPT",
                         "VSP_APP_ALLOWLIST",
                         "VSP_CLIENT_CWD",
                         "VSP_ALLOW_INTEGRITY_MISMATCH",
                     })
            {
                startInfo.Environment.Remove(key);
            }

            startInfo.Environment["VSP_PROFILE"] = profile;

            Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Process.Start 返回 null");

            _ = DrainAsync(process.StandardOutput, line => log.Info($"daemon-stdout: {line}"));
            _ = DrainAsync(process.StandardError, line => log.Warn($"daemon-stderr: {line}"));

            log.Info($"已启动独立 daemon pid={process.Id} profile={profile}");
            return process;
        }
        catch (Exception exception)
        {
            log.Warn($"启动独立 daemon 失败，回退进程内 daemon: {exception.Message}");
            return null;
        }
    }

    private static Process? TryStartBreakaway(string exe, string profile, LogRing log)
    {
        IntPtr environment = IntPtr.Zero;
        IntPtr nul = IntPtr.Zero;

        try
        {
            environment = BuildEnvironmentBlock(profile);
            nul = OpenNulHandle(log);

            if (nul == IntPtr.Zero)
            {
                return null;
            }

            return CreateBreakawayProcess(exe, profile, environment, nul, log);
        }
        catch (Exception exception)
        {
            log.Warn($"breakaway daemon 启动异常，回退普通启动: {exception.Message}");
            return null;
        }
        finally
        {
            if (nul != IntPtr.Zero && nul != new IntPtr(-1))
            {
                CloseHandle(nul);
            }

            if (environment != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environment);
            }
        }
    }

    private static IntPtr OpenNulHandle(LogRing log)
    {
        var inheritable = new SecurityAttributes
        {
            nLength = Marshal.SizeOf<SecurityAttributes>(),
            bInheritHandle = true,
        };
        IntPtr nul = CreateFileW(
            "NUL", GenericReadWrite, FileShareReadWrite, ref inheritable,
            OpenExisting, 0, IntPtr.Zero);

        if (nul != new IntPtr(-1) && nul != IntPtr.Zero)
        {
            return nul;
        }

        log.Warn("独立 daemon breakaway 启动跳过：无法打开 NUL 句柄");
        return IntPtr.Zero;
    }

    private static Process? CreateBreakawayProcess(
        string exe, string profile, IntPtr environment, IntPtr nul, LogRing log)
    {
        var startup = new StartupInfo
        {
            cb = Marshal.SizeOf<StartupInfo>(),
            dwFlags = (int)StartfUseStdHandles,
            hStdInput = nul,
            hStdOutput = nul,
            hStdError = nul,
        };

        string commandLine = $"\"{exe}\" --daemon --profile \"{profile}\"";
        uint flags = CreateBreakawayFromJob | CreateNoWindow | CreateUnicodeEnvironment;

        if (!CreateProcessW(
                exe,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                creationFlags: flags,
                environment: environment,
                currentDirectory: Environment.CurrentDirectory,
                startupInfo: ref startup,
                processInformation: out ProcessInformation info))
        {
            int error = Marshal.GetLastWin32Error();

            if (error != 5)
            {
                log.Warn($"独立 daemon breakaway 启动失败 Win32={error}，回退普通启动");
            }
            else
            {
                log.Info("当前 Job 不允许 breakaway，回退普通 daemon 启动");
            }

            return null;
        }

        return AttachBreakawayProcess(info, profile, log);
    }

    private static Process? AttachBreakawayProcess(ProcessInformation info, string profile, LogRing log)
    {
        try
        {
            CloseHandle(info.hThread);
            CloseHandle(info.hProcess);
            log.Info($"已启动独立 daemon（breakaway）pid={info.dwProcessId} profile={profile}");
            return Process.GetProcessById((int)info.dwProcessId);
        }
        catch (Exception exception)
        {
            log.Warn($"breakaway daemon 已创建但无法获取进程对象: {exception.Message}");
            return null;
        }
    }

    private static IntPtr BuildEnvironmentBlock(string profile)
    {
        var builder = new StringBuilder();
        var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VSP_ALLOW_SCRIPT",
            "VSP_ALLOW_UNSAFE_SCRIPT",
            "VSP_APP_ALLOWLIST",
            "VSP_CLIENT_CWD",
            "VSP_PROFILE",
            "VSP_ALLOW_INTEGRITY_MISMATCH",
        };

        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            string key = entry.Key?.ToString() ?? string.Empty;

            if (key.Length == 0 || skipped.Contains(key))
            {
                continue;
            }

            builder.Append(key).Append('=').Append(entry.Value).Append('\0');
        }

        builder.Append("VSP_PROFILE=").Append(profile).Append('\0');
        builder.Append('\0');
        return Marshal.StringToHGlobalUni(builder.ToString());
    }

    private static async Task DrainAsync(StreamReader reader, Action<string> sink)
    {
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    sink(line);
                }
            }
        }
        catch (Exception exception)
        {
            // daemon 退出时管道关闭。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }
}
