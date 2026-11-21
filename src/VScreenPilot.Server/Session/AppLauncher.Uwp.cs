using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
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
    /// UWP 的顶层是 ApplicationFrameWindow，内容进程（CalculatorApp 等）没有独立 HWND。
    /// 用 UIA 树里的 ProcessId 反查 exe，避免把别的 UWP 窗口当成目标。
    /// </summary>
    private UwpWindow? FindUwpWindowByUia(string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName))
        {
            return null;
        }

        foreach (WindowInfo candidate in WindowManager.ListTopLevel())
        {
            if (!candidate.Visible || !IsApplicationFrame(candidate.Hwnd))
            {
                continue;
            }

            (int pid, string path) = ResolveUwpContentProcess(candidate.Hwnd);

            if (pid > 0 &&
                Path.GetFileNameWithoutExtension(path)
                    .Contains(exeName, StringComparison.OrdinalIgnoreCase))
            {
                return new UwpWindow(candidate, pid, path);
            }
        }

        return null;
    }

    /// <summary>pid 接管时 UWP 内容进程没有顶层窗口，反查承载它的 frame。</summary>
    private WindowInfo? FindUwpFrameForProcess(int pid)
    {
        foreach (WindowInfo candidate in WindowManager.ListTopLevel())
        {
            if (!candidate.Visible || !IsApplicationFrame(candidate.Hwnd))
            {
                continue;
            }

            (int contentPid, _) = ResolveUwpContentProcess(candidate.Hwnd);

            if (contentPid == pid)
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool IsApplicationFrame(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);
        return className.ToString().Contains("ApplicationFrame", StringComparison.OrdinalIgnoreCase);
    }

    private static void CollectProcessIds(UiaTreeNode node, HashSet<int> processIds)
    {
        processIds.Add(node.Info.ProcessId);

        foreach (UiaTreeNode child in node.Children)
        {
            CollectProcessIds(child, processIds);
        }
    }

    private static string GetProcessName(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return string.Empty;
        }
    }

}
