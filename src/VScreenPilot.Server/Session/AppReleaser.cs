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

/// <summary>Close 的提前返回结果与告警。</summary>
internal sealed record CloseOutcome(string? Result, string? Warning);

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
        if (policy == "abort")
        {
            warning = null;
            return "kept";
        }

        // UWP 的 Pid/ExePath 是共享的 ApplicationFrameHost；
        // 必须按内容进程关闭/杀，绝不能强杀 frame host。
        bool uwp = app.ContentPid > 0;
        int closePid = uwp ? app.ContentPid : app.Pid;
        string closePath = uwp ? app.ContentExePath : app.ExePath;

        CloseOutcome outcome = RequestClose(app, policy);
        warning = outcome.Warning;

        if (outcome.Result is not null)
        {
            return outcome.Result;
        }

        return FinalizeClose(app, uwp, closePid, closePath, ref warning);
    }

    private CloseOutcome RequestClose(TrackedApp app, string policy)
    {
        if (policy is "save" or "discard")
        {
            return CloseWithDialog(app, policy);
        }

        NativeMethods.PostMessageW(app.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
        return new CloseOutcome(null, null);
    }

    private CloseOutcome CloseWithDialog(TrackedApp app, string policy)
    {
        // 先走标准 WM_CLOSE：它等价于用户点标题栏 X，不会误点文档/标签页的关闭按钮。
        // 只有窗口忽略 WM_CLOSE 时，才退化为 UIA 查找关闭按钮。
        NativeMethods.PostMessageW(app.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);

        if (!AppTermination.WaitForWindowClosed(app.Hwnd, 500))
        {
            InvokeWindowCloseButton(app.Hwnd);
        }

        AppTermination.WaitForWindowClosed(app.Hwnd, 500);

        if (!NativeMethods.IsWindow(app.Hwnd))
        {
            return new CloseOutcome(null, null);
        }

        return HandleSaveDialog(app, policy);
    }

    private CloseOutcome HandleSaveDialog(TrackedApp app, string policy)
    {
        bool save = policy == "save";
        bool dialogHandled;
        string? warning = null;

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

        if (!dialogHandled)
        {
            // save 的保存框没处理成功时必须保留窗口；discard/force 允许继续走强杀。
            return SaveDialogUnhandledResult(policy) == "kept"
                ? new CloseOutcome("kept", "保存对话框未处理，保留窗口以避免丢未保存内容")
                : new CloseOutcome(null, "未找到保存对话框，将按后续超时策略处理");
        }

        AppTermination.WaitForWindowClosed(app.Hwnd, 3000);

        if (!NativeMethods.IsWindow(app.Hwnd))
        {
            AppTermination.WaitForProcessExit(app.Pid, 1000);
            return new CloseOutcome(save ? "saved" : "discarded", warning);
        }

        return new CloseOutcome(null, $"{policy} 对话框已处理，但窗口仍未关闭");
    }

    private string FinalizeClose(
        TrackedApp app, bool uwp, int closePid, string closePath, ref string? warning)
    {
        if (!AppTermination.WaitForWindowClosed(app.Hwnd, 2500) && NativeMethods.IsWindow(app.Hwnd))
        {
            bool canForce = AppTermination.CanForceClose(
                app.DedicatedProcess,
                app.StartedProcess,
                AppTermination.HasOtherWindows(app.Pid, app.Hwnd));

            if (!canForce)
            {
                warning = $"窗口未能关闭，保留共享进程 {app.Pid}";
                return "kept";
            }

            if (uwp && string.IsNullOrEmpty(closePath))
            {
                warning = "UWP 内容进程路径不可用，拒绝强杀共享 frame host";
                return "kept";
            }

            bool killed = AppTermination.KillProcessTreeIfMatches(closePid, closePath);

            if (killed)
            {
                AppTermination.WaitForProcessExit(closePid, 2000);
            }

            string outcome = ForcedCloseResult(killed, AppTermination.IsProcessAlive(closePid));

            if (outcome == "kept")
            {
                warning = $"已关闭窗口，但进程 {closePid} 无法强杀（exe 路径校验失败），保留进程";
            }

            return outcome;
        }

        if (app.DedicatedProcess && AppTermination.IsProcessAlive(closePid))
        {
            // 窗口已关闭但进程常驻（Office/Edge 常见）；保持原有兜底。
            bool killed = AppTermination.KillProcessTreeIfMatches(closePid, closePath);

            if (!killed)
            {
                warning = $"已关闭窗口，但常驻进程 {closePid} 仍在运行";
            }

            return killed ? "forced" : "closed";
        }

        return "closed";
    }

    /// <summary>保存框未处理成功时的结果：save 必须保留窗口，其他策略继续按强杀处理。</summary>
    internal static string? SaveDialogUnhandledResult(string policy) =>
        policy == "save" ? "kept" : null;

    /// <summary>只有真正发出 kill 才报 forced；进程已退出报 closed，仍存活报 kept。</summary>
    internal static string ForcedCloseResult(bool killed, bool processAlive) =>
        killed ? "forced" : processAlive ? "kept" : "closed";

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
            exception is COMException or InvalidOperationException or ArgumentException)
        {
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

            UiaElementInfo? target = PickDialogButton(buttons, save);

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

    private static UiaElementInfo? PickDialogButton(List<UiaElementInfo> buttons, bool save)
    {
        // 先按 Win32 对话框按钮 ID（是/否/取消 = 6/7/2）识别，避免只支持中英文；
        // 文本匹配只作为兜底。
        string[] preferredIds = save ? new[] { "6", "1" } : new[] { "7", "2" };

        UiaElementInfo? target = buttons.FirstOrDefault(
            item => preferredIds.Contains(item.AutomationId, StringComparer.OrdinalIgnoreCase));

        return target ?? buttons.FirstOrDefault(item => MatchesDialogButton(item, save));
    }

    private static bool MatchesDialogButton(UiaElementInfo item, bool save)
    {
        string name = item.Name ?? string.Empty;

        if (save)
        {
            return IsSaveButton(item, name);
        }

        return IsDiscardButton(name);
    }

    private static bool IsSaveButton(UiaElementInfo item, string name) =>
        !string.Equals(item.AutomationId, "FileSave", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("不保存", StringComparison.OrdinalIgnoreCase) &&
        !name.Contains("Don't Save", StringComparison.OrdinalIgnoreCase) &&
        (name.Contains("保存", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("Save", StringComparison.OrdinalIgnoreCase));

    private static bool IsDiscardButton(string name) =>
        name.Contains("不保存", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("不储存", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Don't Save", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("否", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("放弃", StringComparison.OrdinalIgnoreCase);
}
