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
internal sealed class AppReleaser
{
    private readonly LogRing _log;
    private readonly UiaHost _uia;
    private readonly Action _resetUia;

    public AppReleaser(LogRing log, UiaHost uia, Action resetUia)
    {
        _log = log;
        _uia = uia;
        _resetUia = resetUia;
    }

    public string Close(TrackedApp app, string policy, out string? warning)
    {
        warning = null;

        // UWP 的 Pid/ExePath 是共享的 ApplicationFrameHost；
        // 必须按内容进程关闭/杀，绝不能强杀 frame host。
        bool uwp = app.ContentPid > 0;
        int closePid = uwp ? app.ContentPid : app.Pid;
        string closePath = uwp ? app.ContentExePath : app.ExePath;

        if (policy == "abort")
        {
            return "kept";
        }

        if (policy is "save" or "discard")
        {
            // 先走标准 WM_CLOSE：它等价于用户点标题栏 X，不会误点文档/标签页的关闭按钮。
            // 只有窗口忽略 WM_CLOSE 时，才退化为 UIA 查找关闭按钮。
            NativeMethods.PostMessageW(app.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

            if (!WaitForWindowClosed(app.Hwnd, 500))
            {
                InvokeWindowCloseButton(app.Hwnd);
            }

            WaitForWindowClosed(app.Hwnd, 500);

            if (NativeMethods.IsWindow(app.Hwnd))
            {
                bool save = policy == "save";
                bool dialogHandled;

                // 保存框也走 UIA；如果 provider 卡死，不能让 release 永久挂住。
                Task<bool> dialogTask = Task.Run(
                    () => TryHandleSaveDialog(app.Pid, app.Hwnd, save, 1500));

                if (dialogTask.Wait(3000))
                {
                    dialogHandled = dialogTask.Result;
                }
                else
                {
                    _resetUia();
                    dialogHandled = false;
                    warning = "保存对话框处理超时，已放弃 UIA 并继续清理";
                }

                if (dialogHandled)
                {
                    WaitForWindowClosed(app.Hwnd, 3000);

                    if (!NativeMethods.IsWindow(app.Hwnd))
                    {
                        WaitForProcessExit(app.Pid, 1000);
                        return save ? "saved" : "discarded";
                    }

                    warning = $"{policy} 对话框已处理，但窗口仍未关闭";
                }
                else
                {
                    warning = "未找到保存对话框，将按后续超时策略处理";
                }
            }
        }
        else
        {
            NativeMethods.PostMessageW(app.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        }

        if (!WaitForWindowClosed(app.Hwnd, 2500) && NativeMethods.IsWindow(app.Hwnd))
        {
            if (app.DedicatedProcess ||
                (app.StartedProcess && !HasOtherWindows(app.Pid, app.Hwnd)))
            {
                if (uwp && string.IsNullOrEmpty(closePath))
                {
                    warning = "UWP 内容进程路径不可用，拒绝强杀共享 frame host";
                    return "kept";
                }

                KillProcessTreeIfMatches(closePid, closePath);
                WaitForProcessExit(closePid, 2000);
                return "forced";
            }

            warning = $"窗口未能关闭，保留共享进程 {app.Pid}";
            return "kept";
        }

        if (app.DedicatedProcess && IsProcessAlive(closePid))
        {
            // 窗口已关闭但进程常驻（Office/Edge 常见）；保持原有兜底。
            KillProcessTreeIfMatches(closePid, closePath);
            return "forced";
        }

        return "closed";
    }

    private bool TryHandleSaveDialog(int pid, IntPtr mainHwnd, bool save, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));
        var seen = new List<string>();

        while (DateTime.UtcNow < deadline)
        {
            List<WindowInfo> candidates = WindowManager.ListTopLevel()
                .Where(item => item.Pid == pid && item.Hwnd != mainHwnd)
                .ToList();

            foreach (WindowInfo candidate in candidates)
            {
                if (!seen.Contains(candidate.Title))
                {
                    seen.Add(candidate.Title);
                }

                if (InvokeDialogButton(candidate.Hwnd, save))
                {
                    _log.Info($"已处理保存对话框: class={candidate.ClassName}");
                    return true;
                }
            }

            // Office 的保存提示常常是主窗口内的子对话框。先确认存在“不保存”，
            // 再点“保存/不保存”，避免误点功能区里的保存按钮。
            if (HasDialogButton(mainHwnd) && InvokeDialogButton(mainHwnd, save))
            {
                _log.Info($"已处理主窗口内的保存提示 ({(save ? "save" : "discard")})");
                return true;
            }

            Thread.Sleep(120);
        }

        _log.Warn($"未找到保存对话框；候选窗口: {string.Join(" | ", seen)}");
        return false;
    }

    private bool HasDialogButton(IntPtr hwnd)
    {
        try
        {
            return _uia.FindElements(
                hwnd, "不保存", UiaNameMatch.Exact, "Button", null, 5).Count > 0 ||
                   _uia.FindElements(
                hwnd, "Don't Save", UiaNameMatch.Exact, "Button", null, 5).Count > 0;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    private bool InvokeWindowCloseButton(IntPtr hwnd)
    {
        try
        {
            foreach (string name in new[] { "关闭", "Close" })
            {
                List<UiaElementInfo> buttons = _uia.FindElements(
                    hwnd, name, UiaNameMatch.Exact, "Button", null, 10);

                UiaElementInfo? target = buttons
                    .Where(item => item.Enabled && item.Width > 0 && item.Height > 0)
                    .OrderByDescending(item => item.Left)
                    .FirstOrDefault();

                if (target is null || string.IsNullOrEmpty(target.RuntimeId))
                {
                    continue;
                }

                using UiaElementHandle? handle = _uia.FindByRuntimeId(hwnd, target.RuntimeId);

                if (handle is not null)
                {
                    (bool ok, _) = handle.Invoke();
                    if (ok)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or ArgumentException){
            // 继续走 WM_CLOSE/超时策略。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        return false;
    }

    private bool InvokeDialogButton(IntPtr dialogHwnd, bool save)
    {
        try
        {
            List<UiaElementInfo> buttons = _uia.FindElements(
                dialogHwnd, null, UiaNameMatch.Contains, "Button", null, 30);

            // 先按 Win32 对话框按钮 ID（是/否/取消 = 6/7/2）识别，避免只支持中英文；
            // 文本匹配只作为兜底。
            string[] preferredIds = save
                ? new[] { "6", "1" }
                : new[] { "7", "2" };

            UiaElementInfo? target = buttons.FirstOrDefault(
                item => preferredIds.Contains(item.AutomationId, StringComparer.OrdinalIgnoreCase));

            target ??= buttons.FirstOrDefault(item =>
            {
                string name = item.Name ?? string.Empty;

                if (save)
                {
                    return !string.Equals(item.AutomationId, "FileSave", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("不保存", StringComparison.OrdinalIgnoreCase) &&
                           !name.Contains("Don't Save", StringComparison.OrdinalIgnoreCase) &&
                           (name.Contains("保存", StringComparison.OrdinalIgnoreCase) ||
                            name.Contains("Save", StringComparison.OrdinalIgnoreCase));
                }

                return name.Contains("不保存", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("不儲存", StringComparison.OrdinalIgnoreCase) ||
                       name.Contains("Don't Save", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("否", StringComparison.OrdinalIgnoreCase) ||
                       name.StartsWith("放弃", StringComparison.OrdinalIgnoreCase);
            });

            if (target is null || string.IsNullOrEmpty(target.RuntimeId))
            {
                return false;
            }

            using UiaElementHandle? handle = _uia.FindByRuntimeId(dialogHwnd, target.RuntimeId);

            if (handle is null)
            {
                return false;
            }

            (bool ok, _) = _uia.TryInvoke(handle.Element);
            return ok;
        }
        catch (Exception exception) when (
            exception is COMException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

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
                w => w.Pid == pid && w.Hwnd != exclude && !string.IsNullOrWhiteSpace(w.Title));
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return false;
        }
    }

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

    /// <summary>仅在 PID 仍对应记录的 exe 时才杀进程树，避免 PID 复用误杀。</summary>
    private static readonly HashSet<string> ProtectedSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "applicationframehost", "explorer", "dwm", "sihost", "shellexperiencehost",
        "startmenuexperiencehost", "textinputhost", "searchhost", "taskhostw", "runtimebroker",
    };

    internal static void KillProcessTreeIfMatches(int pid, string expectedPath)
    {
        if (string.IsNullOrEmpty(expectedPath))
        {
            return;
        }

        if (ProtectedSystemProcesses.Contains(Path.GetFileNameWithoutExtension(expectedPath)))
        {
            return;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return;
            }

            string? modulePath = process.MainModule?.FileName;
            if (modulePath is null ||
                !string.Equals(
                    Path.GetFullPath(modulePath),
                    Path.GetFullPath(expectedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception)
        {
            // 进程可能已退出或无法读取模块路径。
            LogSink.Debug($"忽略异常: {exception.Message}");
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
