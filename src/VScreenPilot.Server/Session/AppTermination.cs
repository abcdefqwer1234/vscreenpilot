using System.Diagnostics;
using System.Runtime.InteropServices;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>
/// 应用释放：abort/save/discard/force 策略、WM_CLOSE → UIA 关闭按钮 → 保存对话框、
/// 专用/共享进程与临时 profile 的终止规则。调用方负责遍历与分类汇总。
/// </summary>
/// <summary>进程/窗口终止与目录清理；从 AppReleaser 拆出的系统操作。</summary>
internal static class AppTermination
{
    internal static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return false;
        }
    }

    internal static void WaitForProcessExit(int pid, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));

        while (DateTime.UtcNow < deadline && IsProcessAlive(pid))
        {
            Thread.Sleep(50);
        }
    }

    internal static bool WaitForWindowClosed(IntPtr hwnd, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!NativeMethods.IsWindow(hwnd))
            {
                return true;
            }

            Thread.Sleep(50);
        }

        return !NativeMethods.IsWindow(hwnd);
    }

    internal static bool HasOtherWindows(int pid, IntPtr exclude)
    {
        try
        {
            return WindowManager.ListTopLevel().Any(
                w => w.Pid == pid && w.Hwnd != exclude);
        }
        catch (Exception exception)
        {
            // fail-closed：枚举失败时按“可能还有其他窗口”处理，绝不强杀共享进程。
            LogSink.Debug($"忽略异常: {exception.Message}");
            return true;
        }
    }

    /// <summary>
    /// 是否允许强杀：专用进程可以直接杀；共享进程只有确认没有其他窗口时才允许。
    /// 抽成纯函数便于覆盖真值表（尤其是 hasOtherWindows=true 时绝不强杀）。
    /// </summary>
    internal static bool CanForceClose(bool dedicated, bool started, bool hasOtherWindows) =>
        dedicated || (started && !hasOtherWindows);

    internal static void KillProcessTree(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            // 进程可能已退出。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }

    private static readonly HashSet<string> ProtectedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "explorer", "dwm", "sihost", "shellexperiencehost",
        "startmenuexperiencehost", "textinputhost", "searchhost", "taskhostw", "runtimebroker",
    };

    /// <summary>仅在 PID 仍对应记录的 exe 时才杀进程树，避免 PID 复用误杀。返回是否真的发出了 Kill。</summary>
    internal static bool KillProcessTreeIfMatches(int pid, string expectedPath)
    {
        if (string.IsNullOrEmpty(expectedPath))
        {
            return false;
        }

        if (ProtectedSystemProcesses.Contains(Path.GetFileNameWithoutExtension(expectedPath)))
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return false;
            }

            string? modulePath = process.MainModule?.FileName;
            if (modulePath is null ||
                !string.Equals(
                    Path.GetFullPath(modulePath),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            process.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception exception)
        {
            // 进程可能已退出或无法读取模块路径。
            LogSink.Debug($"忽略异常: {exception.Message}");
            return false;
        }
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception)
        {
            // Edge 可能仍持有锁，留给下次启动清理。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }

}
