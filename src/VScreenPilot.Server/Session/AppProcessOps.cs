using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>ProcessOps：AppLauncher 的拆分子集。</summary>
internal sealed class AppProcessOps
{
    private readonly AppLauncher _core;

    public AppProcessOps(AppLauncher core) => _core = core;

    /// <summary>
    /// 统一启动入口：把 Win32 错误映射成稳定错误码。
    /// 740/5 是提权相关，不能再落到 E_INTERNAL；2/3 是路径问题，193 是可执行格式问题。
    /// </summary>
    internal static Process Start(ProcessStartInfo startInfo)
    {
        try
        {
            return Process.Start(startInfo)
                ?? throw new ToolException("E_APP_START", $"无法启动应用: {startInfo.FileName}");
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            string hint = exception.NativeErrorCode switch
            {
                740 => "该程序要求管理员权限；用宿主 shell 以管理员身份启动，或让用户手动确认 UAC。本工具不会自动提权",
                5 => "访问被拒绝；目标需要更高权限或已被策略拦截，请用管理员身份运行服务端/宿主",
                2 or 3 => "找不到可执行文件；检查 path 与工作目录",
                193 => "不是有效的可执行文件；检查 path 是否指向 exe",
                _ => "检查 path、权限与目标文件是否可执行",
            };

            throw new ToolException(
                exception.NativeErrorCode is 740 or 5 ? "E_ELEVATION_REQUIRED" : "E_APP_START",
                $"启动失败: {startInfo.FileName}（Win32 {exception.NativeErrorCode}: {exception.Message}）",
                new JsonObject
                {
                    ["path"] = startInfo.FileName,
                    ["win32_error"] = exception.NativeErrorCode,
                },
                retryable: false,
                hint: hint,
                stage: "launch");
        }
    }
    /// <summary>
    /// 判断 exe 是否为控制台子系统（PE OptionalHeader.Subsystem == 3）。
    /// 控制台启动器退出后不需要给 GUI 窗口交接留太长宽限，避免 cmd/安装器白等。
    /// 读不到 PE 头时按 GUI 处理（更保守）。
    /// </summary>
    internal static bool IsConsoleImage(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            using FileStream stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40)
            {
                return false;
            }

            stream.Position = 0x3C;
            int peOffset = reader.ReadInt32();

            if (peOffset <= 0 || peOffset + 96L > stream.Length)
            {
                return false;
            }

            stream.Position = peOffset;

            if (reader.ReadUInt32() != 0x00004550)
            {
                return false;
            }

            // COFF header 20 字节后是 OptionalHeader；Subsystem 在 OptionalHeader + 68。
            stream.Position = peOffset + 4 + 20 + 68;
            return reader.ReadUInt16() == 3;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Win11 记事本会恢复上次会话的标签：无参数启动时若标题带文件名，提示调用方先确认。
    /// 只在本次确实启动了新进程时判断，接管旧窗口的场景由 launchWarning 覆盖。
    /// </summary>
    internal static bool LooksLikeRestoredNotepadTitle(
        string exeName, bool started, string? arguments, string? title)
    {
        if (!started ||
            !exeName.Equals("notepad", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(arguments) ||
            string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        // 空白标签的标题是“无标题 - 记事本”/“Untitled - Notepad”。
        if (title.Contains("无标题", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("Untitled", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return title.Contains("记事本", StringComparison.OrdinalIgnoreCase) ||
               title.Contains("Notepad", StringComparison.OrdinalIgnoreCase);
    }

    internal string BuildNewInstanceArguments(
        string exeName, string? arguments, out string? tempProfile)
    {
        tempProfile = null;
        string name = exeName.ToLowerInvariant();

        if (name == "msedge")
        {
            string profile = Path.Combine(
                Path.GetTempPath(), "VScreenPilot", "profiles", "edge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            tempProfile = profile;

            string profileArgs = $"--user-data-dir=\"{profile}\" --no-first-run --no-default-browser-check";
            return string.IsNullOrWhiteSpace(arguments) ? profileArgs : profileArgs + " " + arguments;
        }

        if (name == "winword")
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                return "/n";
            }

            return arguments.Contains("/n", StringComparison.OrdinalIgnoreCase)
                ? arguments
                : "/n " + arguments;
        }

        return arguments ?? string.Empty;
    }

    internal Process? FindRunningProcess(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string fullPath = ResolveFullPath(path);
        Process[] processes;

        try
        {
            processes = Process.GetProcessesByName(name);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return null;
        }

        try
        {
            Process? chosen = SelectProcess(processes, fullPath);
            DisposeAllExcept(processes, chosen);
            return chosen;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            DisposeAllExcept(processes, keep: null);
            return null;
        }
    }

    private string ResolveFullPath(string path)
    {
        try
        {
            return _core._apps.ResolvePath(path);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return path;
        }
    }

    private static Process? SelectProcess(Process[] processes, string fullPath)
    {
        Process? fullMatch = null;
        Process? nameFallback = null;
        Process? pathFallback = null;

        foreach (Process process in processes)
        {
            try
            {
                if (process.HasExited)
                {
                    continue;
                }

                bool samePath = IsSameExecutable(process, fullPath);
                bool hasWindow = process.MainWindowHandle != IntPtr.Zero;
                ClassifyCandidate(process, samePath, hasWindow, ref fullMatch, ref pathFallback, ref nameFallback);
            }
            catch (Exception exception)
            {
                LogSink.Debug($"忽略异常: {exception.Message}");
                nameFallback ??= process;
            }
        }

        // 同名但不同路径的进程只作为最后兜底，避免误接管其他位置的同名程序。
        return fullMatch ?? pathFallback ?? nameFallback;
    }

    private static void ClassifyCandidate(
        Process process,
        bool samePath,
        bool hasWindow,
        ref Process? fullMatch,
        ref Process? pathFallback,
        ref Process? nameFallback)
    {
        if (samePath && hasWindow)
        {
            fullMatch ??= process;
            return;
        }

        if (samePath)
        {
            pathFallback ??= process;
            return;
        }

        if (hasWindow)
        {
            nameFallback ??= process;
        }
    }

    private static void DisposeAllExcept(Process[] processes, Process? keep)
    {
        foreach (Process process in processes)
        {
            if (!ReferenceEquals(process, keep))
            {
                process.Dispose();
            }
        }
    }

    internal static bool IsSameExecutable(Process process, string fullPath)
    {
        try
        {
            string? modulePath = process.MainModule?.FileName;
            return modulePath is not null &&
                   string.Equals(Path.GetFullPath(modulePath), fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return false;
        }
    }

    internal static void CleanupStaleProfiles()
    {
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "VScreenPilot", "profiles");
            if (!Directory.Exists(root))
            {
                return;
            }

            foreach (string directory in Directory.GetDirectories(root, "edge-*"))
            {
                if (Directory.GetCreationTimeUtc(directory) < DateTime.UtcNow.AddDays(-1))
                {
                    AppTermination.TryDeleteDirectory(directory);
                }
            }
        }
        catch (Exception exception)
        {
            // 清理失败不影响启动。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }
}
