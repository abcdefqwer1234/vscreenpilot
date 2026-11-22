using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

/// <summary>click_ref 的 UIA/MSAA 语义通道；从 InputClickOps 拆出。</summary>
internal sealed class InputClickRefOps
{
    internal readonly InputRouter _host;

    public InputClickRefOps(InputRouter host) => _host = host;

    public ClickRefView ClickElementRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput = false)
    {
        DisplayInfo display = _host._display.EnsureDisplay();
        ElementRefEntry probe = _host._element.RequireElement(reference);
        string mode = NormalizeChannel(channel);

        // MSAA 控件没有 UIA 句柄：显式 channel 仍要遵守，不能一律走默认动作。
        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaOrdinal))
        {
            return _host.ClickOps.RefOps.ClickMsaaRef(probe, msaaHwnd, msaaOrdinal, mode, keepForeground, fallbackInput);
        }

        using UiaElementHandle handle = _host._element.ResolveHandle(
            reference, display, out ElementRefEntry entry, out bool refreshed);

        UiaElementInfo info = handle.Info;

        if (mode is "auto" or "uia")
        {
            ClickRefView? invoked = TryInvokeRef(handle, mode, entry, refreshed, info);

            if (invoked is not null)
            {
                return invoked;
            }
        }

        int centerX = info.Left + Math.Max(1, info.Width) / 2;
        int centerY = info.Top + Math.Max(1, info.Height) / 2;
        // 显式 message/input 保持原语义；auto 才允许继续降级（含 fallback_input）。
        string coordinateMode = mode is "input" or "message" ? mode : "auto";
        ClickView clickResult = _host.ClickOps.Click(new SessionClickRequest(
            centerX, centerY, "left", 1, coordinateMode,
            KeepForeground: keepForeground, Observe: true, FallbackInput: fallbackInput));

        return BuildClickRef(entry, refreshed, clickResult);
    }

    private static string NormalizeChannel(string? channel)
    {
        string mode = (channel ?? "auto").Trim().ToLowerInvariant();

        if (mode is "auto" or "uia" or "message" or "input")
        {
            return mode;
        }

        throw new ToolException("E_INVALID_ARG", $"未知 click_ref channel: {mode}");
    }

    private ClickRefView? TryInvokeRef(
        UiaElementHandle handle, string mode, ElementRefEntry entry, bool refreshed, UiaElementInfo info)
    {
        (bool invoked, string? strategy) = handle.Invoke(UiaInvokePattern.Auto);

        if (invoked)
        {
            return new ClickRefView(
                Clicked: true,
                Ref: entry.Ref,
                Channel: "uia",
                X: null, Y: null, Button: null, Clicks: null, Mode: null, Method: null,
                Pattern: strategy,
                InjectMs: null, DurationMs: null, Verified: null, KeepForeground: null,
                Refreshed: refreshed,
                Observed: ObserveAfterAction(entry),
                Bounds: new OptionalBoundsView(info.Left, info.Top, info.Width, info.Height),
                Detail: null,
                Hint: null);
        }

        if (mode != "uia")
        {
            return null;
        }

        throw new ToolException(
            "E_PATTERN_UNSUPPORTED",
            "该控件没有可用的 UIA Invoke/Toggle/Select 模式",
            new JsonObject { ["ref"] = entry.Ref },
            retryable: false,
            hint: "改用 channel=message 或 channel=input",
            stage: "uia");
    }

    private ClickRefView BuildClickRef(ElementRefEntry entry, bool refreshed, ClickView clickResult) =>
        new(
            Clicked: true,
            Ref: entry.Ref,
            Channel: null,
            X: clickResult.X,
            Y: clickResult.Y,
            Button: clickResult.Button,
            Clicks: clickResult.Clicks,
            Mode: clickResult.Mode,
            Method: clickResult.Method,
            Pattern: null,
            InjectMs: clickResult.InjectMs,
            DurationMs: clickResult.DurationMs,
            Verified: clickResult.Verified,
            KeepForeground: clickResult.KeepForeground,
            Refreshed: refreshed,
            Observed: ObserveAfterAction(entry),
            Bounds: null,
            Detail: clickResult.Detail,
            Hint: clickResult.Hint);

    /// <summary>
    /// 动作后的轻量观察：UIA 返回成功不代表应用真的响应了。
    /// 这里只报告控件是否还在/是否可用/是否获得焦点，失败不影响动作结果。
    /// </summary>
    internal ElementObservedView ObserveAfterAction(ElementRefEntry entry)
    {
        try
        {
            using UiaElementHandle? handle = string.IsNullOrEmpty(entry.RuntimeId)
                ? null
                : _host._uia.Uia.FindByRuntimeId(entry.Hwnd, entry.RuntimeId);

            if (handle is null)
            {
                return new ElementObservedView(false, null, null, null, null);
            }

            return new ElementObservedView(
                true,
                handle.Info.Enabled,
                handle.Info.Focused,
                handle.Info.Offscreen,
                null);
        }
        catch (Exception exception)
        {
            return new ElementObservedView(null, null, null, null, exception.Message);
        }
    }

    internal ClickRefView ClickMsaaRef(
        ElementRefEntry probe, IntPtr msaaHwnd, int msaaOrdinal, string mode,
        bool keepForeground, bool fallbackInput)
    {
        bool acted = mode is "auto" or "uia" &&
                     _host._uia.Uia.InvokeMsaa(() => MsaaService.DoDefaultAction(msaaHwnd, msaaOrdinal));
        var bounds = _host._uia.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaOrdinal));
        string resultChannel = acted ? "msaa" : string.Empty;

        if (!acted && mode != "uia" && bounds is not null)
        {
            // auto 在 MSAA 默认动作失败后允许继续走 UIA/消息/输入降级链。
            string msaaMode = mode switch
            {
                "input" => "input",
                "message" => "message",
                _ => "auto",
            };

            ClickView msaaClick = _host.ClickOps.Click(new SessionClickRequest(
                bounds.Value.Left + bounds.Value.Width / 2,
                bounds.Value.Top + bounds.Value.Height / 2,
                "left", 1, msaaMode,
                KeepForeground: keepForeground, Observe: true, FallbackInput: fallbackInput));
            acted = true;
            resultChannel = msaaClick.Method;
        }

        if (!acted)
        {
            throw new ToolException(
                "E_PATTERN_UNSUPPORTED",
                "MSAA 控件没有默认动作，且无法按坐标点击",
                new JsonObject { ["ref"] = probe.Ref });
        }

        return new ClickRefView(
            Clicked: true,
            Ref: probe.Ref,
            Channel: resultChannel,
            X: null, Y: null, Button: null, Clicks: null, Mode: null, Method: null,
            Pattern: null, InjectMs: null, DurationMs: null, Verified: null,
            KeepForeground: null,
            Refreshed: false,
            Observed: new ElementObservedView(true, null, null, null, null),
            Bounds: bounds is null
                ? new OptionalBoundsView(null, null, null, null)
                : new OptionalBoundsView(
                    bounds.Value.Left, bounds.Value.Top, bounds.Value.Width, bounds.Value.Height),
            Detail: null,
            Hint: null);
    }
}
