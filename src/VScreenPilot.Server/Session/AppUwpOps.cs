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

/// <summary>UwpOps：AppLauncher 的拆分子集。</summary>
internal sealed class AppUwpOps
{
    private readonly AppLauncher _core;

    public AppUwpOps(AppLauncher core) => _core = core;
    /// <summary>
    /// UWP 的顶层是 ApplicationFrameWindow，内容进程（CalculatorApp 等）没有独立 HWND。
    /// 用 UIA 树里的 ProcessId 反查 exe，避免把别的 UWP 窗口当成目标。
    /// </summary>
    internal AppWindowOps.UwpWindow? FindUwpWindowByUia(string exeName)
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

            (int pid, string path) = _core.ResolveUwpContentProcess(candidate.Hwnd);

            if (pid > 0 &&
                Path.GetFileNameWithoutExtension(path)
                    .Contains(exeName, StringComparison.OrdinalIgnoreCase))
            {
                return new AppWindowOps.UwpWindow(candidate, pid, path);
            }
        }

        return null;
    }

    /// <summary>pid 接管时 UWP 内容进程没有顶层窗口，反查承载它的 frame。</summary>
    internal WindowInfo? FindUwpFrameForProcess(int pid)
    {
        foreach (WindowInfo candidate in WindowManager.ListTopLevel())
        {
            if (!candidate.Visible || !IsApplicationFrame(candidate.Hwnd))
            {
                continue;
            }

            (int contentPid, _) = _core.ResolveUwpContentProcess(candidate.Hwnd);

            if (contentPid == pid)
            {
                return candidate;
            }
        }

        return null;
    }

    internal bool IsApplicationFrame(IntPtr hwnd)
    {
        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);
        return className.ToString().Contains("ApplicationFrame", StringComparison.OrdinalIgnoreCase);
    }

    internal void CollectProcessIds(UiaTreeNode node, HashSet<int> processIds)
    {
        processIds.Add(node.Info.ProcessId);

        foreach (UiaTreeNode child in node.Children)
        {
            CollectProcessIds(child, processIds);
        }
    }

    internal string GetProcessName(int pid)
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
