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

namespace VScreenPilot.Session;

internal sealed partial class AppLauncher
{
    /// <summary>该窗口是否已由当前客户端接管；用于区分“用户原有窗口”和“本会话早前启动的窗口”。</summary>
    private bool IsOwnedWindowOnCurrentClient(IntPtr hwnd)
    {
        lock (_gate)
        {
            return _host.Apps.Any(app => _host.IsOwned(app) && app.Hwnd == hwnd && _host.IsLive(app));
        }
    }

    private void AssignWindowProcessToJob(int pid)
    {
        // 启动壳进程会转交给真实进程；重试几次再判定失败，避免把正常的交接记成兜底不可用。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var process = Process.GetProcessById(pid);

                if (process.HasExited)
                {
                    Thread.Sleep(150);
                    continue;
                }

                if (_host.Job.Assign(process))
                {
                    _log.Info($"已将进程 {pid} 加入作业对象");
                    return;
                }

                Thread.Sleep(150);
            }
            catch
            {
                // 进程可能在分配前已经退出/转交。
                Thread.Sleep(150);
            }
        }

        if (!_jobUnavailableLogged)
        {
            _jobUnavailableLogged = true;
            _log.Warn(
                $"进程 {pid} 未能加入作业对象（{_host.Job.Status}）；崩溃兜底不可用，" +
                "将依赖进程树清理与启动时孤儿清扫");
        }
    }

    private WindowInfo? WaitForNewWindow(
        Process process, string exeName, HashSet<IntPtr> before, int waitMs,
        out int contentPid, out string contentPath)
    {
        contentPid = 0;
        contentPath = string.Empty;
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, waitMs));

        while (DateTime.UtcNow < deadline)
        {
            List<WindowInfo> candidates = WindowManager.ListTopLevel()
                .Where(item => item.Visible &&
                               !before.Contains(item.Hwnd) &&
                               item.Area >= 100 * 80 &&
                               !string.IsNullOrWhiteSpace(item.Title))
                .ToList();

            WindowInfo? byPid = candidates
                .Where(item => item.Pid == process.Id)
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (byPid is not null)
            {
                Thread.Sleep(300);
                return WindowManager.FromHandle(byPid.Hwnd) ?? byPid;
            }

            WindowInfo? byName = candidates
                .Where(item => string.Equals(
                    GetProcessName(item.Pid), exeName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (byName is not null)
            {
                Thread.Sleep(300);
                return WindowManager.FromHandle(byName.Hwnd) ?? byName;
            }

            // UWP：calc.exe 只是启动器，真正窗口属于 ApplicationFrameWindow。
            // 先认内容进程与 exe 匹配的宿主（可复用已存在的 frame），
            // 再退化为“新出现的 ApplicationFrameWindow 窗口”。
            WindowInfo? byUwp = candidates
                .Where(item => item.ClassName.Contains(
                    "ApplicationFrame", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (byUwp is not null)
            {
                (contentPid, contentPath) = ResolveUwpContentProcess(byUwp.Hwnd);
                Thread.Sleep(300);
                return WindowManager.FromHandle(byUwp.Hwnd) ?? byUwp;
            }

            Thread.Sleep(100);
        }

        // 超时前再做一次 UIA 探测：UWP 内容进程没有独立 HWND，
        // 只能从 frame 的语义树里找 ProcessId。
        UwpWindow? uwp = FindUwpWindowByUia(exeName);

        if (uwp is not null)
        {
            contentPid = uwp.ContentPid;
            contentPath = uwp.ContentPath;
            return uwp.Window;
        }

        return null;
    }

    private sealed record UwpWindow(WindowInfo Window, int ContentPid, string ContentPath);

    private WindowInfo? WaitForMainWindow(Process process, int waitMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, waitMs));
        WindowInfo? found = null;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                throw new ToolException("E_APP_EXITED", $"应用在窗口就绪前退出 (exit={process.ExitCode})");
            }

            found = WindowManager.FindMainWindow(process.Id);
            if (found is not null)
            {
                break;
            }

            Thread.Sleep(100);
        }

        if (found is null)
        {
            throw new ToolException(
                "E_WINDOW_TIMEOUT",
                $"等待主窗口超时 ({waitMs}ms, pid={process.Id})");
        }

        Thread.Sleep(400);
        return WindowManager.FindMainWindow(process.Id) ?? found;
    }

    private WindowInfo? WaitForStableWindow(WindowInfo window, int waitMs, int displayArea)
    {
        // 启动 splash/引导页通常远小于主窗口。小于虚拟屏 10% 的窗口先不当作主窗口，
        // 继续轮询同进程的最大窗口，直到出现更大的窗口或超时。
        // 启动 splash 的窗口面积可能并不小（CCW 的 splash 约 920×460），
        // 用“不足虚拟屏一半”作为启动期窗口的判定，最多观察 12 秒等待主窗口。
        bool smallWindow = displayArea > 0 && window.Area < displayArea * 0.50;
        var deadline = DateTime.UtcNow.AddMilliseconds(
            smallWindow
                ? Math.Max(3000, Math.Min(waitMs, 20000))
                : Math.Max(500, Math.Min(waitMs, 3000)));

        _log.Info(
            $"等待窗口稳定: hwnd=0x{window.Hwnd.ToInt64():X} area={window.Area} " +
            $"displayArea={displayArea} small={smallWindow} waitMs={waitMs}");

        int stable = 0;
        long lastArea = window.Area;

        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(150);

            // 不能用 FindMainWindow 的 owner 过滤：部分应用的 splash 是主窗口的
            // owner，会导到主窗口被排除、一直选中 splash。这里直接按 pid + 面积选。
            WindowInfo? next = WindowManager.ListTopLevel()
                .Where(item => item.Pid == window.Pid &&
                               item.Visible &&
                               item.Area >= 100 * 80 &&
                               !string.IsNullOrWhiteSpace(item.Title))
                .OrderByDescending(item => item.Area)
                .FirstOrDefault();

            if (next is null)
            {
                // 启动 splash 关闭后到主窗口出现之间可能没有可见窗口；
                // 只要进程还活着就继续等，别把已失效的 splash 当成主窗口返回。
                if (!AppReleaser.IsProcessAlive(window.Pid))
                {
                    break;
                }

                _log.Info($"等待窗口稳定: 暂无可选窗口，继续等待 pid={window.Pid}");
                continue;
            }

            if (next.Hwnd != window.Hwnd || next.Area > lastArea * 1.1 || next.Area < lastArea * 0.9)
            {
                window = next;
                lastArea = next.Area;
                stable = 0;
                smallWindow = displayArea > 0 && window.Area < displayArea * 0.50;
                // 不记录标题：可能含文档名/客户名。
                _log.Info(
                    $"窗口更新: hwnd=0x{window.Hwnd.ToInt64():X} area={window.Area} small={smallWindow}");
                continue;
            }

            stable++;
            if (!smallWindow && stable >= 2)
            {
                _log.Info($"窗口已稳定: hwnd=0x{window.Hwnd.ToInt64():X} area={window.Area}");
                return window;
            }
        }

        WindowInfo? final = WindowManager.ListTopLevel()
            .Where(item => item.Pid == window.Pid && item.Visible)
            .OrderByDescending(item => item.Area)
            .FirstOrDefault() ?? window;

        _log.Info($"等待窗口稳定结束: hwnd=0x{final.Hwnd.ToInt64():X} area={final.Area}");

        return final;
    }

}
