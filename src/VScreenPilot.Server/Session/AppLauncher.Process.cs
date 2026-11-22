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

internal sealed partial class AppLauncher
{
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

    private static string BuildNewInstanceArguments(
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

    private Process? FindRunningProcess(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = _apps.ResolvePath(path);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            fullPath = path;
        }

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

        Process? fullMatch = null;
        Process? nameFallback = null;
        Process? pathFallback = null;

        try
        {
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

                    if (samePath && hasWindow)
                    {
                        fullMatch ??= process;
                    }
                    else if (samePath)
                    {
                        pathFallback ??= process;
                    }
                    else if (hasWindow)
                    {
                        nameFallback ??= process;
                    }
                }
                catch (Exception exception)
                {
                    LogSink.Debug($"忽略异常: {exception.Message}");
                    nameFallback ??= process;
                }
            }

            // 同名但不同路径的进程只作为最后兜底，避免误接管其他位置的同名程序。
            Process? chosen = fullMatch ?? pathFallback ?? nameFallback;

            foreach (Process process in processes)
            {
                if (!ReferenceEquals(process, chosen))
                {
                    process.Dispose();
                }
            }

            return chosen;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            foreach (Process process in processes)
            {
                process.Dispose();
            }

            return null;
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
                    AppReleaser.TryDeleteDirectory(directory);
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
