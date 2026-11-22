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

/// <summary>坐标点击的入参。</summary>
internal sealed record SessionClickRequest(
    int X,
    int Y,
    string Button,
    int Clicks,
    string Mode,
    bool KeepForeground = false,
    bool Observe = true,
    bool FallbackInput = false);

/// <summary>ClickOps：InputRouter 的输入子通道。</summary>
internal sealed class InputClickOps
{
    internal readonly InputRouter _host;

    internal readonly InputClickRefOps RefOps;

    public InputClickOps(InputRouter host)
    {
        _host = host;
        RefOps = new InputClickRefOps(host);
    }
    /// <summary>按 element_ref 点击：优先 UIA pattern，其次消息/输入通道。</summary>
    public ClickRefView ClickElementRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput = false) =>
        RefOps.ClickElementRef(reference, channel, keepForeground, fallbackInput);

    public ClickView Click(SessionClickRequest request)
    {
        DisplayInfo display = _host._display.EnsureDisplay();
        string mode = NormalizeClickMode(request);
        bool allowInputFallback = InputPolicy.AllowsInputFallback(request.FallbackInput);

        using var inputLease = InputLease.TryAcquire(3000, "click");
        EnsureInteractiveIfNeeded(mode);

        TrackedApp app = _host._element.RequirePointInTrackedApp(display, request.X, request.Y, "click");

        var observeWatch = Stopwatch.StartNew();
        ObserveSignature? observeBefore = request.Observe
            ? _host._display.CaptureSignature(display, request.X - 60, request.Y - 60, 120, 120)
            : null;

        var stopwatch = Stopwatch.StartNew();
        IntPtr previousForeground = WindowManager.GetForegroundWindow();

        if (request.KeepForeground)
        {
            // 菜单/弹出层需要连续操作时，暂时冻结焦点护栏；租约到期后自动恢复，
            // 避免异常路径上 agent 不再调用工具时永久关闭护栏。
            _host._focus.PauseFocusGuard(30000);
        }

        bool canUseUia = request.Button.Equals("left", StringComparison.OrdinalIgnoreCase) &&
                         request.Clicks == 1;
        string method = InjectClick(
            request, app, mode, allowInputFallback, canUseUia, out string? detail);

        double injectMs = stopwatch.Elapsed.TotalMilliseconds;
        SettleFocus(request.KeepForeground, previousForeground);

        ObservedView? observed = CaptureClickObservation(display, request, observeWatch, observeBefore);

        return new ClickView(
            request.X,
            request.Y,
            request.Button,
            request.Clicks,
            mode,
            method,
            Math.Round(injectMs, 2),
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            observed?.Changed ?? false,
            request.KeepForeground,
            observed,
            detail,
            method == "message"
                ? "消息点击可能被部分应用忽略，请用 wait_for/assert_region 验证"
                : null);
    }

    private static string NormalizeClickMode(SessionClickRequest request)
    {
        if (request.Clicks is < 1 or > 10)
        {
            throw new ToolException("E_INVALID_ARG", "clicks 必须在 1..10 之间");
        }

        string mode = (request.Mode ?? "auto").ToLowerInvariant();

        if (mode is "auto" or "uia" or "message" or "input")
        {
            return mode;
        }

        throw new ToolException("E_INVALID_ARG", $"未知 click mode: {mode}");
    }

    private void EnsureInteractiveIfNeeded(string mode)
    {
        if (mode == "input" && !_host._display.IsInputDesktopAvailable())
        {
            throw NoInteractiveDesktop();
        }
    }

    private void SettleFocus(bool keepForeground, IntPtr previousForeground)
    {
        // 大多数点击不会改变前台；只有窗口消息/应用自身激活才需要额外的焦点稳定与归还。
        // keep_foreground=true 时保留目标窗口焦点，供菜单/弹出层连续操作。
        Thread.Sleep(50);

        if (keepForeground || WindowManager.GetForegroundWindow() == previousForeground)
        {
            return;
        }

        Thread.Sleep(50);
        _host.RestoreFocusAfterClick(previousForeground);
    }

    private ObservedView? CaptureClickObservation(
        DisplayInfo display,
        SessionClickRequest request,
        Stopwatch observeWatch,
        ObserveSignature? observeBefore)
    {
        ObserveSignature? observeAfter = observeBefore is null
            ? null
            : _host._display.CaptureSignature(display, request.X - 60, request.Y - 60, 120, 120);

        return _host._uia.BuildObserved(
            observeBefore, observeAfter, observeWatch.Elapsed.TotalMilliseconds);
    }

    internal static ToolException NoInteractiveDesktop() =>
        new(
            "E_NO_INTERACTIVE_DESKTOP",
            "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
            retryable: true,
            hint: "解锁桌面或改用 UIA 语义操作",
            stage: "input");

    internal string InjectClick(
        SessionClickRequest request,
        TrackedApp app,
        string mode,
        bool allowInputFallback,
        bool canUseUia,
        out string? detail)
    {
        detail = null;

        switch (mode)
        {
            case "input":
                InjectInput(request);
                return "input";

            case "uia":
                if (!canUseUia)
                {
                    throw new ToolException("E_INVALID_ARG", "UIA 模式仅支持左键单击");
                }

                if (!_host.UiaGuardOps.TryUiaInvoke(request.X, request.Y, app.Pid))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "该坐标没有 UIA InvokePattern，无法在不移动光标的前提下点击");
                }

                return "uia";

            case "message":
                _host.RejectNonClientArea(app, request.X, request.Y, "message");

                if (!MessageInput.Click(
                        request.X, request.Y, request.Button, request.Clicks, out detail))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "窗口消息注入失败",
                        new JsonObject { ["detail"] = detail });
                }

                return "message";

            case "auto":
            default:
                return InjectAuto(request, app, allowInputFallback, canUseUia, out detail);
        }
    }

    internal string InjectAuto(
        SessionClickRequest request,
        TrackedApp app,
        bool allowInputFallback,
        bool canUseUia,
        out string? detail)
    {
        detail = null;
        bool nonClient = _host.IsNonClientPoint(app.Hwnd, request.X, request.Y);

        if (canUseUia && _host.UiaGuardOps.TryUiaInvoke(request.X, request.Y, app.Pid))
        {
            return "uia";
        }

        if (!nonClient &&
            MessageInput.Click(
                request.X, request.Y, request.Button, request.Clicks, out detail))
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

        InjectInput(request);
        return "input-fallback";
    }

    internal void InjectInput(SessionClickRequest request)
    {
        _host._focus.PauseCursor();
        _host._focus.PauseFocusGuard(0);

        try
        {
            InputInjector.Click(request.X, request.Y, request.Button, request.Clicks);
        }
        finally
        {
            _host._focus.ResumeCursor();

            if (!request.KeepForeground)
            {
                _host._focus.ResumeFocusGuard();
            }
        }
    }

}
