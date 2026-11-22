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

/// <summary>ValueOps：ElementOperations 的拆分子集。</summary>
internal sealed class ElementValueOps
{
    private readonly ElementOperations _host;

    public ElementValueOps(ElementOperations host) => _host = host;
    public SetElementValueView SetElementValue(
        string reference, string value, bool allowKeyboard, string? mode = null, bool allowDestructive = false)
    {
        DisplayInfo display = _host._apps.EnsureDisplay();
        ElementRefEntry probe = _host._state.Resolver.Require(reference);

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
        string? existing = _host._state.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId));

        if (!string.IsNullOrEmpty(existing) &&
            !string.Equals(existing, value, StringComparison.Ordinal) &&
            !allowDestructive)
        {
            throw new ToolException(
                "E_DESTRUCTIVE_REQUIRED",
                "MSAA 控件已有内容；覆盖请显式传 allow_destructive=true",
                new JsonObject { ["ref"] = probe.Ref, ["existing_length"] = existing.Length });
        }

        if (!_host._state.Uia.InvokeMsaa(() => MsaaService.SetValue(msaaHwnd, msaaChildId, value)))
        {
            if (!allowKeyboard)
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    "MSAA 控件不支持 accValue 写入；可改用 allow_keyboard=true 的键盘路径",
                    new JsonObject { ["ref"] = probe.Ref });
            }

            var bounds = _host._state.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaChildId));

            if (bounds is null)
            {
                throw new ToolException(
                    "E_ELEMENT_OFFSCREEN",
                    "MSAA 控件没有可用位置，无法切换到键盘写入",
                    new JsonObject { ["ref"] = probe.Ref });
            }

            _host._input.Click(
                bounds.Value.Left + bounds.Value.Width / 2,
                bounds.Value.Top + bounds.Value.Height / 2,
                "left", 1, "message");

            TypeTextView typed = _host._input.TypeText(value, null, verify: true, chunkChars: 32, channel: "auto");
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

        string? observed = _host._state.Uia.InvokeMsaa(() => MsaaService.GetValue(msaaHwnd, msaaChildId));
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
        using UiaElementHandle handle = _host._state.Resolver.Resolve(reference, display, out ElementRefEntry entry, out bool refreshed);
        UiaElementInfo info = handle.Info;

        if (!info.Enabled)
        {
            throw new ToolException(
                "E_ELEMENT_DISABLED",
                "控件已禁用，拒绝写入",
                new JsonObject { ["ref"] = entry.Ref, ["element"] = ElementPresenter.ToJson(info) });
        }

        string effectiveMode = _host.KeyboardValueOps.ResolveWriteMode(mode, info);

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

            return _host.KeyboardValueOps.SetElementValueByKeyboard(
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

        return _host.KeyboardValueOps.SetElementValueByKeyboard(
            display, handle, entry, value, refreshed, mode, allowDestructive);
    }
}
