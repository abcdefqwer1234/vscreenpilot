using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Views;
using Interop.UIAutomationClient;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Json;
using VScreenPilot.Interop;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using static VScreenPilot.Json.JsonArgReader;
using ObserveSignature = VScreenPilot.Verification.ObserveSampler.Signature;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>ClickOps：InputRouter 的输入子通道。</summary>
internal sealed class InputClickOps
{
    internal readonly InputRouter _host;

    public InputClickOps(InputRouter host) => _host = host;
    /// <summary>按 element_ref 点击：优先 UIA pattern，其次消息/输入通道。</summary>
    public ClickRefView ClickElementRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput = false)
    {
        DisplayInfo display = _host._display.EnsureDisplay();
        ElementRefEntry probe = _host._element.RequireElement(reference);
        string mode = (channel ?? "auto").Trim().ToLowerInvariant();

        if (mode is not ("auto" or "uia" or "message" or "input"))
        {
            throw new ToolException("E_INVALID_ARG", $"未知 click_ref channel: {mode}");
        }

        // MSAA 控件没有 UIA 句柄：显式 channel 仍要遵守，不能一律走默认动作。
        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaOrdinal))
        {
            return ClickMsaaRef(probe, msaaHwnd, msaaOrdinal, mode, keepForeground, fallbackInput);
        }

        using UiaElementHandle handle = _host._element.ResolveHandle(
            reference, display, out ElementRefEntry entry, out bool refreshed);

        UiaElementInfo info = handle.Info;

        if (mode is "auto" or "uia")
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

            if (mode == "uia")
            {
                throw new ToolException(
                    "E_PATTERN_UNSUPPORTED",
                    "该控件没有可用的 UIA Invoke/Toggle/Select 模式",
                    new JsonObject { ["ref"] = entry.Ref },
                    retryable: false,
                    hint: "改用 channel=message 或 channel=input",
                    stage: "uia");
            }
        }

        int centerX = info.Left + Math.Max(1, info.Width) / 2;
        int centerY = info.Top + Math.Max(1, info.Height) / 2;
        // 显式 message/input 保持原语义；auto 才允许继续降级（含 fallback_input）。
        string coordinateMode = mode is "input" or "message" ? mode : "auto";
        ClickView clickResult = Click(
            centerX, centerY, "left", 1, coordinateMode,
            keepForeground, observe: true, fallbackInput: fallbackInput);

        return new ClickRefView(
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
    }

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

    public ClickView Click(
        int x, int y, string button, int clicks, string mode,
        bool keepForeground = false, bool observe = true, bool fallbackInput = false)
    {
        DisplayInfo display = _host._display.EnsureDisplay();

        if (clicks is < 1 or > 10)
        {
            throw new ToolException("E_INVALID_ARG", "clicks 必须在 1..10 之间");
        }

        mode = (mode ?? "auto").ToLowerInvariant();
        if (mode is not ("auto" or "uia" or "message" or "input"))
        {
            throw new ToolException("E_INVALID_ARG", $"未知 click mode: {mode}");
        }

        bool allowInputFallback = InputPolicy.AllowsInputFallback(fallbackInput);

        using var inputLease = InputLease.TryAcquire(3000, "click");

        if (mode == "input" && !_host._display.IsInputDesktopAvailable())
        {
            throw NoInteractiveDesktop();
        }

        TrackedApp app = _host._element.RequirePointInTrackedApp(display, x, y, "click");

        var observeWatch = Stopwatch.StartNew();
        ObserveSignature? observeBefore = observe
            ? _host._display.CaptureSignature(display, x - 60, y - 60, 120, 120)
            : null;

        var stopwatch = Stopwatch.StartNew();
        IntPtr previousForeground = WindowManager.GetForegroundWindow();

        if (keepForeground)
        {
            // 菜单/弹出层需要连续操作时，暂时冻结焦点护栏；租约到期后自动恢复，
            // 避免异常路径上 agent 不再调用工具时永久关闭护栏。
            _host._focus.PauseFocusGuard(30000);
        }

        bool canUseUia = button.Equals("left", StringComparison.OrdinalIgnoreCase) && clicks == 1;
        string method = InjectClick(
            app, x, y, button, clicks, mode, keepForeground, allowInputFallback, canUseUia, out string? detail);

        double injectMs = stopwatch.Elapsed.TotalMilliseconds;

        // 大多数点击不会改变前台；只有窗口消息/应用自身激活才需要额外的焦点稳定与归还。
        // keep_foreground=true 时保留目标窗口焦点，供菜单/弹出层连续操作。
        Thread.Sleep(50);
        if (!keepForeground && WindowManager.GetForegroundWindow() != previousForeground)
        {
            Thread.Sleep(50);
            _host.RestoreFocusAfterClick(previousForeground);
        }

        ObserveSignature? observeAfter = observeBefore is null
            ? null
            : _host._display.CaptureSignature(display, x - 60, y - 60, 120, 120);
        ObservedView? observed = _host._uia.BuildObserved(
            observeBefore, observeAfter, observeWatch.Elapsed.TotalMilliseconds);

        return new ClickView(
            x,
            y,
            button,
            clicks,
            mode,
            method,
            Math.Round(injectMs, 2),
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            observed?.Changed ?? false,
            keepForeground,
            observed,
            detail,
            method == "message"
                ? "消息点击可能被部分应用忽略，请用 wait_for/assert_region 验证"
                : null);
    }

    internal static ToolException NoInteractiveDesktop() =>
        new(
            "E_NO_INTERACTIVE_DESKTOP",
            "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
            retryable: true,
            hint: "解锁桌面或改用 UIA 语义操作",
            stage: "input");

    internal string InjectClick(
        TrackedApp app, int x, int y, string button, int clicks, string mode,
        bool keepForeground, bool allowInputFallback, bool canUseUia, out string? detail)
    {
        detail = null;

        switch (mode)
        {
            case "input":
                InjectInput(x, y, button, clicks, keepForeground);
                return "input";

            case "uia":
                if (!canUseUia)
                {
                    throw new ToolException("E_INVALID_ARG", "UIA 模式仅支持左键单击");
                }

                if (!_host.UiaGuardOps.TryUiaInvoke(x, y, app.Pid))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "该坐标没有 UIA InvokePattern，无法在不移动光标的前提下点击");
                }

                return "uia";

            case "message":
                _host.RejectNonClientArea(app, x, y, "message");

                if (!MessageInput.Click(x, y, button, clicks, out detail))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "窗口消息注入失败",
                        new JsonObject { ["detail"] = detail });
                }

                return "message";

            case "auto":
            default:
                return InjectAuto(app, x, y, button, clicks, keepForeground, allowInputFallback,
                    canUseUia, out detail);
        }
    }

    internal string InjectAuto(
        TrackedApp app, int x, int y, string button, int clicks,
        bool keepForeground, bool allowInputFallback, bool canUseUia, out string? detail)
    {
        detail = null;

        if (canUseUia && _host.UiaGuardOps.TryUiaInvoke(x, y, app.Pid))
        {
            return "uia";
        }

        if (!_host.IsNonClientPoint(app.Hwnd, x, y) &&
            MessageInput.Click(x, y, button, clicks, out detail))
        {
            return "message";
        }

        if (!allowInputFallback)
        {
            throw new ToolException(
                "E_INPUT_UNSUPPORTED",
                "无法在不移动光标的前提下点击；如接受光标跳变，请显式使用 mode=\"input\" 或 fallback_input=true",
                new JsonObject { ["detail"] = detail });
        }

        // fallback 也要检查交互桌面：锁屏/安全桌面上 SendInput 只会得到
        // 模糊的 E_INPUT，提前给出 E_NO_INTERACTIVE_DESKTOP 更有用。
        if (!_host._display.IsInputDesktopAvailable())
        {
            throw NoInteractiveDesktop();
        }

        InjectInput(x, y, button, clicks, keepForeground);
        return "input-fallback";
    }

    internal void InjectInput(int x, int y, string button, int clicks, bool keepForeground)
    {
        _host._focus.PauseCursor();
        _host._focus.PauseFocusGuard(0);

        try
        {
            InputInjector.Click(x, y, button, clicks);
        }
        finally
        {
            _host._focus.ResumeCursor();

            if (!keepForeground)
            {
                _host._focus.ResumeFocusGuard();
            }
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

            ClickView msaaClick = Click(
                bounds.Value.Left + bounds.Value.Width / 2,
                bounds.Value.Top + bounds.Value.Height / 2,
                "left", 1, msaaMode, keepForeground, observe: true, fallbackInput: fallbackInput);
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
