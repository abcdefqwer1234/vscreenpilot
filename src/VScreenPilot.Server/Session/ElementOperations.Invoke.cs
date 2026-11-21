using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Diagnostics;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Input;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

internal sealed partial class ElementOperations
{
    public InvokeElementView InvokeElement(string reference, bool allowClick, string? pattern = null)
    {
        DisplayInfo display = _host.EnsureDisplay();
        ElementRefEntry probe = _host.Resolver.Require(reference);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            bool invoked = _host.Uia.InvokeMsaa(() => MsaaService.DoDefaultAction(msaaHwnd, msaaChildId));

            if (!invoked && allowClick)
            {
                var bounds = _host.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaChildId));

                if (bounds is not null)
                {
                    invoked = MessageInput.Click(
                        bounds.Value.Left + bounds.Value.Width / 2,
                        bounds.Value.Top + bounds.Value.Height / 2,
                        "left", 1, out _);
                }
            }

            if (!invoked)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    "MSAA 控件不支持默认动作；可改用 click 或 allow_click=true",
                    new JsonObject { ["ref"] = probe.Ref });
            }

            return new InvokeElementView(
                probe.Ref, "msaa", Invoked: true, Verified: false, ControlType: null,
                Refreshed: false, Detail: null, Element: null);
        }

        UiaElementHandle handle = _host.Resolver.Resolve(reference, display, out ElementRefEntry entry, out bool refreshed);
        bool handleTransferred = false;

        try
        {
        UiaElementInfo info = handle.Info;

        UiaInvokePattern mode = ParseInvokePattern(pattern);

        if (!info.Enabled)
        {
            throw new ToolException(
                "E_ELEMENT_DISABLED",
                "控件已禁用，拒绝触发",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        if (mode == UiaInvokePattern.Default)
        {
            return InvokeDefaultAction(display, handle, entry, refreshed);
        }

        // Invoke 是同步 COM 调用：按钮弹出模态对话框时会一直等到对话框关闭。
        // 在独立任务里执行，超时后放弃旧 UIA 实例，避免整个会话永久卡死。
        Task<(bool Ok, string? Strategy)> invokeTask = Task.Run(() =>
        {
            try
            {
                return handle.Invoke(mode);
            }
            finally
            {
                handle.Dispose();
            }
        });

        handleTransferred = true;

        if (!invokeTask.Wait(5000))
        {
            _host.ResetUia();
            throw new ToolException(
                "E_TIMEOUT",
                "UIA 触发 5s 未返回，常见于会打开模态对话框的按钮",
                new JsonObject { ["ref"] = entry.Ref },
                retryable: true,
                hint: "先用 get_ui_tree 或截图确认是否已弹出对话框；也可改用 click 的消息点击",
                stage: "invoke");
        }

        (bool ok, string? strategy) = invokeTask.Result;

        if (ok)
        {
            Thread.Sleep(50);
            return new InvokeElementView(
                entry.Ref, strategy ?? "uia", null, null, null, refreshed, null,
                ElementPresenter.ToView(info, compact: true));
        }

        if (!allowClick)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件没有可用的 UIA 触发模式（Invoke/Toggle/SelectionItem/ExpandCollapse/Legacy）；" +
                "可先用 pattern 指定期望模式；如需按坐标触发，请改用 click 或显式传 allow_click=true",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        int centerX = info.Left + info.Width / 2;
        int centerY = info.Top + info.Height / 2;

        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new ToolException(
                "E_ELEMENT_OFFSCREEN",
                "控件没有可见尺寸，无法按坐标触发",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host.RequirePointInTrackedApp(display, centerX, centerY, "invoke_element");

        if (!MessageInput.Click(centerX, centerY, "left", 1, out string? detail))
        {
            throw new ToolException(
                "E_INPUT_UNSUPPORTED",
                "控件不支持语义触发，按坐标点击也失败",
                new JsonObject { ["ref"] = entry.Ref, ["detail"] = detail });
        }

        Thread.Sleep(50);
        return new InvokeElementView(
            entry.Ref, "message_click", null, null, null, refreshed, detail,
            ElementPresenter.ToView(info, compact: true));
        }
        finally
        {
            if (!handleTransferred)
            {
                handle.Dispose();
            }
        }
    }

    private static UiaInvokePattern ParseInvokePattern(string? pattern) =>
        (pattern ?? "auto").Trim().ToLowerInvariant() switch
        {
            "" or "auto" => UiaInvokePattern.Auto,
            "invoke" => UiaInvokePattern.Invoke,
            "toggle" => UiaInvokePattern.Toggle,
            "select" or "selectionitem" => UiaInvokePattern.Select,
            "expand" or "expandcollapse" => UiaInvokePattern.Expand,
            "legacy" or "legacyiaccessible" => UiaInvokePattern.Legacy,
            "default" or "open" or "defaultaction" => UiaInvokePattern.Default,
            _ => throw new ToolException(
                "E_INVALID_ARG",
                $"未知 pattern: {pattern}（可选 auto/invoke/toggle/select/expand/legacy/default）"),
        };

    /// <summary>
    /// 显式“打开/默认动作”：聚焦控件后发送 Space+Enter（树节点）或 Enter。
    /// 用于 WinForms 树节点等没有 InvokePattern、只能靠键盘默认动作打开的场景。
    /// </summary>
    private InvokeElementView InvokeDefaultAction(
        DisplayInfo display,
        UiaElementHandle handle,
        ElementRefEntry entry,
        bool refreshed)
    {
        TrackedApp? app;

        app = _host.OwnedAppForPid(entry.Pid);

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法执行默认动作",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host.RequireOnDisplay(display, app.Hwnd, "invoke_element");

        IntPtr previous = WindowManager.GetForegroundWindow();
        _host.PauseFocusGuard();

        try
        {
            if (!_host.EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切换到前台，已取消默认动作",
                    new JsonObject { ["ref"] = entry.Ref },
                    retryable: true,
                    hint: "如果当前有模态框，先处理弹窗再重试",
                    stage: "focus");
            }

            try
            {
                handle.Focus();
            }
            catch (Exception exception) when (
                exception is COMException or InvalidOperationException){
                // 部分 WinForms/WPF 控件不支持 SetFocus；继续用 Enter 尝试。
                System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
            }

            Thread.Sleep(80);

            bool treeItem = string.Equals(
                handle.Info.ControlTypeName, "TreeItem", StringComparison.OrdinalIgnoreCase);

            if (treeItem)
            {
                InputInjector.PressKeys("Space");
                Thread.Sleep(80);
            }

            InputInjector.PressKeys("Enter");
            Thread.Sleep(250);

            return new InvokeElementView(
                entry.Ref, "default_action", null, null, handle.Info.ControlTypeName, refreshed, null,
                ElementPresenter.ToView(handle.Info, compact: false));
        }
        finally
        {
            _host.RestoreForeground(previous);
            _host.ResumeFocusGuard();
        }
    }

}
