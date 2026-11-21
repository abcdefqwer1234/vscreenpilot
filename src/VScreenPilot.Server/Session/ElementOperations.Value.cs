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
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

internal sealed partial class ElementOperations
{
    public SetElementValueView SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null, bool allowDestructive = false)
    {
        DisplayInfo display = _host.EnsureDisplay();
        ElementRefEntry probe = _host.Resolver.Require(reference);

        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaChildId))
        {
            return SetMsaaValue(probe, msaaHwnd, msaaChildId, value, allowKeyboard, mode, allowDestructive);
        }

        return SetUiaValue(display, reference, value, allowKeyboard, mode, allowDestructive);
    }

    private SetElementValueView SetMsaaValue(
        ElementRefEntry probe, IntPtr msaaHwnd, int msaaChildId, string value,
        bool allowKeyboard, string? mode, bool allowDestructive)
    {
        string? existing = _host.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId));

        if (!string.IsNullOrEmpty(existing) &&
            !string.Equals(existing, value, StringComparison.Ordinal) &&
            !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "MSAA 控件已有内容；覆盖请显式传 allow_destructive=true",
                new JsonObject { ["ref"] = probe.Ref, ["existing_length"] = existing.Length });
        }

        if (!_host.Uia.InvokeMsaa(() => MsaaService.SetValue(msaaHwnd, msaaChildId, value)))
        {
            if (!allowKeyboard)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    "MSAA 控件不支持 accValue 写入；可改用 allow_keyboard=true 的键盘路径",
                    new JsonObject { ["ref"] = probe.Ref });
            }

            var bounds = _host.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaChildId));

            if (bounds is null)
            {
                throw new ToolException(
                    "E_ELEMENT_OFFSCREEN",
                    "MSAA 控件没有可用位置，无法切换到键盘写入",
                    new JsonObject { ["ref"] = probe.Ref });
            }

            _host.Click(
                bounds.Value.Left + bounds.Value.Width / 2,
                bounds.Value.Top + bounds.Value.Height / 2,
                "left", 1, "message");

            TypeTextView typed = _host.TypeText(value, null, verify: true, chunkChars: 32, channel: "auto");
            return new SetElementValueView(
                probe.Ref,
                "msaa+keyboard",
                mode ?? "auto",
                Destructive: null,
                typed.Verified,
                value,
                typed.ObservedLength is null ? null : JsonValue.Create(typed.ObservedLength.Value),
                Refreshed: false,
                Chunks: null,
                DurationMs: null,
                ObservedLength: null,
                Warning: null);
        }

        string? observed = _host.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId));
        return new SetElementValueView(
            probe.Ref,
            "msaa",
            mode ?? "auto",
            Destructive: null,
            string.Equals(observed, value, StringComparison.Ordinal),
            value,
            observed is null ? null : JsonValue.Create(observed),
            Refreshed: false,
            Chunks: null,
            DurationMs: null,
            ObservedLength: null,
            Warning: null);
    }

    private SetElementValueView SetUiaValue(
        DisplayInfo display, string reference, string value,
        bool allowKeyboard, string? mode, bool allowDestructive)
    {
        using UiaElementHandle handle = _host.Resolver.Resolve(reference, display, out ElementRefEntry entry, out bool refreshed);
        UiaElementInfo info = handle.Info;

        if (!info.Enabled)
        {
            throw new ToolException(
                "E_ELEMENT_DISABLED",
                "控件已禁用，拒绝写入",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        string effectiveMode = ResolveWriteMode(mode, info);

        if (effectiveMode == "replace" && !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "replace 会先全选目标内容再覆盖；请显式传 allow_destructive=true，或改用 mode=append/insert",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["mode"] = effectiveMode,
                    ["control_type"] = info.ControlTypeName,
                    ["automation_id"] = info.AutomationId,
                });
        }

        // ValuePattern 只能整值替换，无法追加；append/replace 必须走键盘路径，
        // 否则会在支持 ValuePattern 的控件上静默覆盖已有内容。
        if (effectiveMode is "append" or "replace")
        {
            if (!allowKeyboard)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    effectiveMode == "append"
                        ? "append 无法通过 ValuePattern 实现（只能整值替换）；请显式传 allow_keyboard=true"
                        : "replace 需要键盘写入（Ctrl+A 覆盖）；请显式传 allow_keyboard=true，或改用 mode=insert",
                    new JsonObject
                    {
                        ["ref"] = entry.Ref,
                        ["mode"] = effectiveMode,
                        ["element"] = ElementPresenter.ToJson(info),
                    });
            }

            return SetElementValueByKeyboard(
                display, handle, entry, value, refreshed, effectiveMode, allowDestructive);
        }

        // ValuePattern 的 SetValue 是整值覆盖。mode=insert 在普通控件上走这条路径，
        // 因此必须先看现有内容：非空且不同就要求显式 allow_destructive。
        // 避免把“插入”误当成安全操作而静默清空用户已有输入。
        string? existingValue = handle.GetValue();
        bool destructiveInsert = effectiveMode == "insert" &&
                                 !string.IsNullOrEmpty(existingValue) &&
                                 !string.Equals(existingValue, value, StringComparison.Ordinal);

        if (destructiveInsert && !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "insert 在 ValuePattern 控件上是整值写入，会覆盖现有内容；" +
                "请显式传 allow_destructive=true，或改用 mode=append / allow_keyboard=true",
                new JsonObject
                {
                    ["ref"] = entry.Ref,
                    ["mode"] = effectiveMode,
                    ["existing_length"] = existingValue?.Length ?? 0,
                    ["control_type"] = info.ControlTypeName,
                    ["automation_id"] = info.AutomationId,
                });
        }

        return FinishUiaWrite(
            handle.SetValue(value), handle, entry, info, display, value,
            allowKeyboard, effectiveMode, destructiveInsert, refreshed, allowDestructive, mode);
    }

    private SetElementValueView FinishUiaWrite(
        UiaSetValueResult result,
        UiaElementHandle handle,
        ElementRefEntry entry,
        UiaElementInfo info,
        DisplayInfo display,
        string value,
        bool allowKeyboard,
        string effectiveMode,
        bool destructiveInsert,
        bool refreshed,
        bool allowDestructive,
        string? mode)
    {

        if (result.Failure is not null)
        {
            throw new ToolException(
                "E_UIA",
                $"ValuePattern 写值失败: {result.Failure}",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        if (result.Supported && result.ReadOnly)
        {
            throw new ToolException(
                "E_ELEMENT_READONLY",
                "控件为只读，无法通过 ValuePattern 写入",
                new JsonObject { ["ref"] = entry.Ref, ["value"] = result.Value });
        }

        if (result.Supported)
        {
            bool verified = string.Equals(result.Value ?? string.Empty, value, StringComparison.Ordinal);
            return new SetElementValueView(
                entry.Ref,
                "value_pattern",
                effectiveMode,
                destructiveInsert,
                verified,
                value,
                result.Value is null ? null : JsonValue.Create(result.Value),
                refreshed,
                Chunks: null,
                DurationMs: null,
                ObservedLength: null,
                Warning: null);
        }

        if (!allowKeyboard)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "该控件不支持 ValuePattern；如接受切换前台后键入，请显式传 allow_keyboard=true",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        return SetElementValueByKeyboard(
            display, handle, entry, value, refreshed, mode, allowDestructive);
    }


    private SetElementValueView SetElementValueByKeyboard(
        DisplayInfo display,
        UiaElementHandle handle,
        ElementRefEntry entry,
        string value,
        bool refreshed,
        string? mode,
        bool allowDestructive)
    {
        (TrackedApp app, string effectiveMode, bool replace) =
            ResolveKeyboardTarget(display, entry, handle, mode, allowDestructive);


        IntPtr previous = WindowManager.GetForegroundWindow();
        _host.PauseFocusGuard();

        try
        {
            if (!_host.EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切到前台，已取消键入以避免输入到用户窗口",
                    new JsonObject { ["ref"] = entry.Ref },
                    retryable: true,
                    hint: "确认没有模态框禁用主窗口；可改用消息点击或先关闭弹窗",
                    stage: "focus");
            }

            bool focused = TryFocusElement(handle, display);

            if (!focused)
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把焦点设置到控件，已取消键入",
                    new JsonObject { ["ref"] = entry.Ref },
                    retryable: true,
                    stage: "focus");
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            if (replace)
            {
                InputInjector.PressKeys("Ctrl+A");
                Thread.Sleep(60);

                if (value.Length == 0)
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
                            break;
                        }

                        if (attempt + 1 < 3)
                        {
                            InputInjector.PressKeys("Ctrl+A");
                            Thread.Sleep(50);
                        }
                    }
                }
            }
            else if (effectiveMode == "append")
            {
                InputInjector.PressKeys("Ctrl+End");
            }

            Thread.Sleep(30);
            TextWriteResult write = _host.WriteTextVerified(
                app, handle, value, replace, timeoutMs: 12000);

            string? after = handle.GetText(65536);
            bool verified = write.Verified;

            return new SetElementValueView(
                entry.Ref,
                "keyboard",
                effectiveMode,
                replace,
                verified,
                value,
                after is null ? null : JsonValue.Create(after),
                refreshed,
                write.Chunks,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                after?.Length,
                !verified
                    ? "键盘写入未能通过 TextPattern/ValuePattern 回读确认；请用 get_element_text 或截图复核"
                    : null);
        }
        finally
        {
            _host.RestoreForeground(previous);
            _host.ResumeFocusGuard();
        }
    }

    private (TrackedApp App, string EffectiveMode, bool Replace) ResolveKeyboardTarget(
        DisplayInfo display, ElementRefEntry entry, UiaElementHandle handle,
        string? mode, bool allowDestructive)
    {
        TrackedApp? app;

        app = _host.OwnedAppForPid(entry.Pid);

        if (app is null)
        {
            throw new ToolException(
                "E_NO_TARGET",
                "element_ref 所属应用不在本会话中，无法键入",
                new JsonObject { ["ref"] = entry.Ref });
        }

        _host.RequireOnDisplay(display, entry.Hwnd, "set_element_value");

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
    private static string ResolveWriteMode(string? mode, UiaElementInfo info)
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

    private bool TryFocusElement(UiaElementHandle handle, DisplayInfo display)
    {
        try
        {
            handle.Focus();
            Thread.Sleep(40);
            return VerifyFocus(handle);
        }
        catch (Exception exception) when (
            exception is System.Runtime.InteropServices.COMException or InvalidOperationException){
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
    private bool VerifyFocus(UiaElementHandle expected)
    {
        using UiaElementHandle? focused = _host.Uia.GetFocusedElementHandle(expected.Info.ProcessId);
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
