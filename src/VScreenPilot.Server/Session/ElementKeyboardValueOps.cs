using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Input;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Json;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>KeyboardValueOps：ElementOperations 的拆分子集。</summary>
internal sealed class ElementKeyboardValueOps
{
    internal readonly ElementOperations _host;

    public ElementKeyboardValueOps(ElementOperations host) => _host = host;
    internal SetElementValueView SetElementValueByKeyboard(ElementWriteRequest request)
    {
        (TrackedApp app, string effectiveMode, bool replace) = ResolveKeyboardTarget(
            request.Display, request.Entry, request.Handle, request.Mode, request.AllowDestructive);

        IntPtr previous = WindowManager.GetForegroundWindow();
        _host._focus.PauseFocusGuard();

        try
        {
            EnsureForegroundAndFocus(app, request.Handle, request.Display, request.Entry);

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            PrepareInput(replace, effectiveMode, request.Handle, request.Value);

            Thread.Sleep(30);
            TextWriteResult write = _host._input.WriteTextVerified(
                app, request.Handle, new TextWriteRequest(request.Value, replace, TimeoutMs: 12000));

            return BuildKeyboardView(request, effectiveMode, replace, write, stopwatch);
        }
        finally
        {
            _host._focus.RestoreForeground(previous);
            _host._focus.ResumeFocusGuard();
        }
    }

    private void EnsureForegroundAndFocus(
        TrackedApp app, UiaElementHandle handle, DisplayInfo display, ElementRefEntry entry)
    {
        if (!_host._focus.EnsureForeground(app))
        {
            throw new ToolException(
                "E_FOCUS",
                "无法把目标窗口切到前台，已取消键入以避免输入到用户窗口",
                new JsonObject { ["ref"] = entry.Ref },
                retryable: true,
                hint: "确认没有模态框禁用主窗口；可改用消息点击或先关闭弹窗",
                stage: "focus");
        }

        if (TryFocusElement(handle, display))
        {
            return;
        }

        throw new ToolException(
            "E_FOCUS",
            "无法把焦点设置到控件，已取消键入",
            new JsonObject { ["ref"] = entry.Ref },
            retryable: true,
            stage: "focus");
    }

    private static void PrepareInput(bool replace, string effectiveMode, UiaElementHandle handle, string value)
    {
        if (!replace)
        {
            if (effectiveMode == "append")
            {
                InputInjector.PressKeys("Ctrl+End");
            }

            return;
        }

        InputInjector.PressKeys("Ctrl+A");
        Thread.Sleep(60);

        if (value.Length == 0)
        {
            ClearSelectedText(handle);
        }
    }

    private static void ClearSelectedText(UiaElementHandle handle)
    {
        // 新版记事本的 Document 控件对 Ctrl+A→Delete 的处理是异步的；
        // 连续发送可能丢掉 Delete，导致“清空”实际未生效。
        for (int attempt = 0; attempt < 3; attempt++)
        {
            InputInjector.PressKeys(attempt == 0 ? "Delete" : "Backspace");
            Thread.Sleep(80);

            string? probe = handle.GetText(32);

            if (string.IsNullOrEmpty(probe))
            {
                return;
            }

            if (attempt + 1 >= 3)
            {
                continue;
            }

            InputInjector.PressKeys("Ctrl+A");
            Thread.Sleep(50);
        }
    }

    private static SetElementValueView BuildKeyboardView(
        ElementWriteRequest request,
        string effectiveMode,
        bool replace,
        TextWriteResult write,
        System.Diagnostics.Stopwatch stopwatch)
    {
        string? after = request.Handle.GetText(65536);
        bool verified = write.Verified;

        return new SetElementValueView(
            request.Entry.Ref,
            "keyboard",
            effectiveMode,
            replace,
            verified,
            request.Value,
            after is null ? null : JsonValue.Create(after),
            request.Refreshed,
            write.Chunks,
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            after?.Length,
            verified
                ? null
                : "键盘写入未能通过 TextPattern/ValuePattern 回读确认；请用 get_element_text 或截图复核");
    }

    internal (TrackedApp App, string EffectiveMode, bool Replace) ResolveKeyboardTarget(
        DisplayInfo display, ElementRefEntry entry, UiaElementHandle handle,
        string? mode, bool allowDestructive)
    {
        TrackedApp? app;

        app = _host._apps.OwnedAppForPid(entry.Pid);

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法键入",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host._apps.RequireOnDisplay(display, entry.Hwnd, "set_element_value");

        string effectiveMode = ResolveWriteMode(mode, handle.Info);
        bool replace = effectiveMode == "replace";

        if (replace && !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "replace 会先全选目标内容再覆盖；请显式传 allow_destructive=true，或改用 mode=append/insert",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["mode"] = effectiveMode,
                    ["control_type"] = handle.Info.ControlTypeName,
                    ["automation_id"] = handle.Info.AutomationId,
                });
        }

        return (app, effectiveMode, replace);
    }

    /// <summary>
    /// auto：文档类控件默认追加（避免覆盖），普通控件默认插入；
    /// replace 必须显式 allow_destructive=true。
    /// </summary>
    internal string ResolveWriteMode(string? mode, UiaElementInfo info)
    {
        string requested = (mode ?? "auto").Trim().ToLowerInvariant();

        if (requested is "replace" or "append" or "insert")
        {
            return requested;
        }

        if (requested is not ("auto" or ""))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 set_element_value mode: {mode}（可选 auto/append/insert/replace）");
        }

        bool documentLike = info.ControlTypeName == "Document" ||
                            string.Equals(info.AutomationId, "Body", StringComparison.OrdinalIgnoreCase);

        return documentLike ? "append" : "insert";
    }

    internal bool TryFocusElement(UiaElementHandle handle, DisplayInfo display)
    {
        try
        {
            handle.Focus();
            Thread.Sleep(40);
            return VerifyFocus(handle);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Word 正文的 Edit/Body 不支持 UIA SetFocus；退化到消息点击元素中心。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        UiaElementInfo info = handle.Info;
        int centerX = info.Left + info.Width / 2;
        int centerY = info.Top + info.Height / 2;

        if (centerX < display.X || centerX >= display.X + display.Width ||
            centerY < display.Y || centerY >= display.Y + display.Height)
        {
            return false;
        }

        if (!MessageInput.Click(centerX, centerY, "left", 1, out _))
        {
            return false;
        }

        Thread.Sleep(80);
        return VerifyFocus(handle);
    }

    /// <summary>SetFocus/消息点击后确认焦点确实落在目标控件上，避免键入到相邻控件。</summary>
    internal bool VerifyFocus(UiaElementHandle expected)
    {
        using UiaElementHandle? focused = _host._state.Uia.GetFocusedElementHandle(expected.Info.ProcessId);
        if (focused is null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(expected.Info.RuntimeId) &&
            string.Equals(focused.Info.RuntimeId, expected.Info.RuntimeId, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(focused.Info.AutomationId, expected.Info.AutomationId, StringComparison.Ordinal) &&
               string.Equals(focused.Info.ControlTypeName, expected.Info.ControlTypeName, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(focused.Info.Name, expected.Info.Name, StringComparison.Ordinal);
    }
}
