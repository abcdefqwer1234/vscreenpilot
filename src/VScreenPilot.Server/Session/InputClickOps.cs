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
using VScreenPilot.Platform.Logging;
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
    bool FallbackInput = false,
    bool ContextMenu = false,
    int IntervalMs = 50,
    bool AllowDestructive = false,
    int SettleMs = 0,
    bool AbortOnWindowChange = true);

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
        string reference, string? channel, bool keepForeground, bool fallbackInput = false,
        bool allowDestructive = false) =>
        RefOps.ClickElementRef(reference, channel, keepForeground, fallbackInput, allowDestructive);

    public ClickView Click(SessionClickRequest request)
    {
        DisplayInfo display = _host._display.EnsureDisplay();
        string mode = NormalizeClickMode(request);
        bool allowInputFallback = InputPolicy.AllowsInputFallback(request.FallbackInput);

        if (request.ContextMenu &&
            !request.Button.Equals("right", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                "context_menu 只能配合 button=right 使用",
                retryable: false,
                hint: "右键点击时传 button=right + context_menu=true",
                stage: "validate");
        }

        using var inputLease = InputLease.TryAcquire(3000, "click");
        EnsureInteractiveIfNeeded(mode);

        TrackedApp app = _host._element.RequirePointInTrackedApp(display, request.X, request.Y, "click");
        ElevationGuard.RequireNotElevated(app.Pid, "click");

        var stopwatch = Stopwatch.StartNew();
        IntPtr previousForeground = WindowManager.GetForegroundWindow();

        // 注入前做只读命中测试：既用于报告 hit_element，也用于破坏性目标护栏。
        (UiaElementInfo? hitInfo, HitElementView? hitView, string hitSource) =
            ProbeHit(request.X, request.Y, app.Pid);
        hitView = DestructiveTargetGuard.Check(
            hitInfo, request.X, request.Y, request.AllowDestructive, "click") ?? hitView;
        FocusElementView? focusBefore = CaptureFocus(app.Pid);

        var observeWatch = Stopwatch.StartNew();
        ObserveSignature? observeBefore = request.Observe
            ? _host._display.CaptureSignature(display, request.X - 60, request.Y - 60, 120, 120)
            : null;

        if (request.KeepForeground)
        {
            // 菜单/弹出层需要连续操作时，申请会话级租约；租约到期后自动恢复，
            // 避免异常路径上 agent 不再调用工具时永久关闭护栏。
            _host.Lease.Acquire(
                _host._session.Clients.CurrentId,
                app.Pid,
                app.Hwnd.ToInt64(),
                ForegroundLease.DefaultTtlMs,
                exclusive: false,
                source: "keep_foreground",
                previousForeground: previousForeground);
            _host._focus.PauseFocusGuard(ForegroundLease.DefaultTtlMs);
        }

        bool canUseUia = request.Button.Equals("left", StringComparison.OrdinalIgnoreCase) &&
                         request.Clicks == 1;
        int clicksDone = request.Clicks;
        WindowChange? opened = null;
        string method;
        string? detail;
        UiaElementInfo? invokeHit;

        if (request.Clicks > 1)
        {
            HashSet<IntPtr> beforeWindows = request.AbortOnWindowChange
                ? WindowChangeDetector.Capture(app.Pid)
                : new HashSet<IntPtr>();
            (method, detail, invokeHit, clicksDone, opened) = InjectMultiClick(
                request, app, mode, allowInputFallback, beforeWindows,
                request.AbortOnWindowChange);
        }
        else
        {
            method = InjectClick(
                request, app, mode, allowInputFallback, canUseUia, hitInfo,
                out detail, out UiaElementInfo? singleHit);
            invokeHit = singleHit;
        }

        if (hitInfo is null && invokeHit is not null)
        {
            hitInfo = invokeHit;
            hitView = DestructiveTargetGuard.Describe(invokeHit);
            hitSource = "uia";
        }

        double injectMs = stopwatch.Elapsed.TotalMilliseconds;
        SettleFocus(request.KeepForeground, previousForeground);

        ObservedView? observed = CaptureClickObservation(display, request, observeWatch, observeBefore);
        FocusElementView? focusAfter = CaptureFocus(app.Pid);
        bool? focusChanged = focusBefore is null && focusAfter is null
            ? null
            : !string.Equals(focusBefore?.RuntimeId, focusAfter?.RuntimeId, StringComparison.Ordinal);
        string delivery = method switch
        {
            "uia" => "uia-invoked",
            "message" => "message-posted",
            "input" => "input-sent",
            "input-fallback" => "input-fallback",
            _ => method,
        };
        bool? suspectNoop = observed is null
            ? null
            : method is "message" or "input" or "input-fallback" &&
              !observed.Changed &&
              focusChanged != true &&
              (hitInfo is not null || hitSource == "window");

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
                : null,
            hitView,
            hitSource,
            focusBefore,
            focusAfter,
            focusChanged,
            delivery,
            suspectNoop,
            clicksDone,
            opened is not null,
            opened is null
                ? null
                : new OpenedDialogView(
                    opened.Hwnd.ToInt64(), opened.Title, opened.ClassName));
    }

    private FocusElementView? CaptureFocus(int pid)
    {
        try
        {
            UiaElementInfo? info = _host._uia.Uia.GetFocusedElement();

            return info is null || info.ProcessId != pid
                ? null
                : new FocusElementView(
                    info.ProcessId,
                    info.Name,
                    info.AutomationId,
                    info.ControlTypeName,
                    info.RuntimeId);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"读取焦点元素失败: {exception.Message}");
            return null;
        }
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

        if (_host.Lease.IsActive(_host._session.Clients.CurrentId))
        {
            return;
        }

        Thread.Sleep(50);
        _host.RestoreFocusAfterClick(previousForeground);
    }

    /// <summary>
    /// 只读命中测试：优先 UIA ElementFromPoint；UIA 不可用时退回 WindowFromPoint，
    /// 至少报告点到了哪个窗口。失败不阻塞点击。
    /// </summary>
    private (UiaElementInfo? Info, HitElementView? View, string Source) ProbeHit(
        int x, int y, int pid)
    {
        UiaElementInfo? info = _host.UiaGuardOps.DescribeAtPoint(x, y, pid);

        if (info is not null)
        {
            return (info, DestructiveTargetGuard.Describe(info), "uia");
        }

        IntPtr hwnd = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        WindowInfo? window = hwnd == IntPtr.Zero ? null : WindowManager.FromHandle(hwnd);

        if (window is null)
        {
            return (null, null, "none");
        }

        var view = new HitElementView(
            string.IsNullOrWhiteSpace(window.Title) ? window.ClassName : window.Title,
            string.Empty,
            "Window",
            new BoundsView(window.Rect.Left, window.Rect.Top, window.Width, window.Height),
            true,
            false);

        return (null, view, "window");
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

    private static Func<UiaElementInfo, bool> InvokeGuard(SessionClickRequest request) =>
        info =>
        {
            DestructiveTargetGuard.Check(
                info, request.X, request.Y, request.AllowDestructive, "click");
            return true;
        };

    /// <summary>
    /// 多击逐击执行：每击后检查是否出现新的顶层窗口，出现则中止剩余点击。
    /// 这样第一击打开模态框时，第二击不会落到对话框的表格/按钮上。
    /// </summary>
    private (string Method, string? Detail, UiaElementInfo? Hit, int ClicksDone, WindowChange? Opened)
        InjectMultiClick(
            SessionClickRequest request,
            TrackedApp app,
            string mode,
            bool allowInputFallback,
            HashSet<IntPtr> beforeWindows,
            bool abortOnWindowChange)
    {
        var single = request with { Clicks = 1 };
        string method = string.Empty;
        string? detail = null;
        UiaElementInfo? hit = null;
        int done = 0;

        for (int index = 0; index < request.Clicks; index++)
        {
            method = InjectClick(
                single, app, mode, allowInputFallback, canUseUia: false, hit,
                out string? stepDetail, out UiaElementInfo? stepHit);
            detail = stepDetail ?? detail;
            hit = stepHit ?? hit;
            done++;

            if (index >= request.Clicks - 1)
            {
                break;
            }

            int gap = Math.Clamp(Math.Max(request.IntervalMs, request.SettleMs), 10, 2000);
            Thread.Sleep(gap);

            if (!abortOnWindowChange)
            {
                continue;
            }

            WindowChange? change = WindowChangeDetector.FindNew(app.Pid, beforeWindows);

            if (change is not null)
            {
                return (
                    method,
                    $"第 {done + 1} 击前检测到新顶层窗口 hwnd=0x{change.Hwnd.ToInt64():X}；已中止剩余点击",
                    hit,
                    done,
                    change);
            }
        }

        return (method, detail, hit, done, null);
    }

    internal string InjectClick(
        SessionClickRequest request,
        TrackedApp app,
        string mode,
        bool allowInputFallback,
        bool canUseUia,
        UiaElementInfo? preHit,
        out string? detail,
        out UiaElementInfo? hit)
    {
        detail = null;
        hit = preHit;

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

                UiaInvokeOutcome explicitUia =
                    _host.UiaGuardOps.TryUiaInvoke(
                        request.X, request.Y, app.Pid, out UiaElementInfo? explicitHit,
                        InvokeGuard(request));
                hit = explicitHit ?? hit;

                if (explicitUia == UiaInvokeOutcome.TimedOut)
                {
                    throw UiaTimedOut();
                }

                if (explicitUia != UiaInvokeOutcome.Ok)
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "该坐标没有 UIA InvokePattern，无法在不移动光标的前提下点击");
                }

                return "uia";

            case "message":
                _host.RejectNonClientArea(app, request.X, request.Y, "message");

                if (!MessageInput.Click(
                        request.X, request.Y, request.Button, request.Clicks, out detail,
                        request.ContextMenu))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "窗口消息注入失败",
                        new JsonObject { ["detail"] = detail });
                }

                return "message";

            case "auto":
            default:
                return InjectAuto(
                    request, app, allowInputFallback, canUseUia, preHit, out detail, out hit);
        }
    }

    internal string InjectAuto(
        SessionClickRequest request,
        TrackedApp app,
        bool allowInputFallback,
        bool canUseUia,
        UiaElementInfo? preHit,
        out string? detail,
        out UiaElementInfo? hit)
    {
        detail = null;
        hit = preHit;
        bool nonClient = _host.IsNonClientPoint(app.Hwnd, request.X, request.Y);

        if (canUseUia)
        {
            UiaInvokeOutcome uia =
                _host.UiaGuardOps.TryUiaInvoke(
                    request.X, request.Y, app.Pid, out UiaElementInfo? uiaHit,
                    InvokeGuard(request));
            hit = uiaHit ?? hit;

            if (uia == UiaInvokeOutcome.Ok)
            {
                return "uia";
            }

            if (uia == UiaInvokeOutcome.TimedOut)
            {
                // 超时后 UIA 调用可能仍会落地；再补一次 message/input 会变成双重点击。
                throw UiaTimedOut();
            }
        }

        if (!nonClient &&
            MessageInput.Click(
                request.X, request.Y, request.Button, request.Clicks, out detail,
                request.ContextMenu))
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

    private static ToolException UiaTimedOut() =>
        new(
            "E_TIMEOUT",
            "UIA 坐标触发超时，已熔断该进程；本次不会再执行 message/input 回退点击",
            retryable: true,
            hint: "稍后重试；若应用持续卡顿，显式使用 mode=\"message\" 或 mode=\"input\"",
            stage: "uia");

    internal void InjectInput(SessionClickRequest request)
    {
        _host._focus.PauseCursor();
        _host._focus.PauseFocusGuard(0);

        try
        {
            InputInjector.Click(
                request.X, request.Y, request.Button, request.Clicks, request.IntervalMs);
        }
        finally
        {
            _host._focus.ResumeCursor();

            if (!request.KeepForeground &&
                !_host.Lease.IsActive(_host._session.Clients.CurrentId))
            {
                _host._focus.ResumeFocusGuard();
            }
        }
    }

}
