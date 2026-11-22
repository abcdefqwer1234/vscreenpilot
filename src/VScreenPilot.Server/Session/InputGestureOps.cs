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

/// <summary>一次 gesture 调用的公共上下文。</summary>
internal sealed record GestureContext(
    DisplayInfo Display, bool Observe, bool UseInput, Stopwatch Stopwatch, IntPtr PreviousForeground);

/// <summary>拖拽两端的坐标。</summary>
internal readonly record struct DragPoints(int FromX, int FromY, int ToX, int ToY);

/// <summary>GestureOps：InputRouter 的输入子通道。</summary>
internal sealed class InputGestureOps
{
    internal readonly InputRouter _host;

    public InputGestureOps(InputRouter host) => _host = host;
    public GestureView Gesture(JsonElement arguments)
    {
        DisplayInfo display = _host._display.Ensure();

        string type = GetString(arguments, "type") ?? "drag";
        string button = GetString(arguments, "button") ?? "left";
        string mode = (GetString(arguments, "mode") ?? "auto").ToLowerInvariant();
        int duration = GetInt(arguments, "duration_ms", 300);
        bool observe = GetBool(arguments, "observe", true);
        var stopwatch = Stopwatch.StartNew();
        IntPtr previousForeground = WindowManager.GetForegroundWindow();

        if (mode == "uia")
        {
            throw new ToolException("E_INVALID_ARG", "gesture 不支持 uia 模式");
        }

        if (mode is not ("auto" or "message" or "input"))
        {
            throw new ToolException("E_INVALID_ARG", $"未知 gesture mode: {mode}");
        }

        using var inputLease = InputLease.TryAcquire(3000, "gesture");

        bool useInput = mode == "input";

        if (useInput && !_host._display.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }

        var context = new GestureContext(display, observe, useInput, stopwatch, previousForeground);

        return type.ToLowerInvariant() switch
        {
            "drag" => GestureDrag(arguments, button, duration, context),
            "scroll" => GestureScroll(arguments, context),
            "move" => GestureMove(arguments, context),
            _ => throw new ToolException("E_INVALID_ARG", $"未知 gesture 类型: {type}"),
        };
    }

    internal GestureView GestureDrag(
        JsonElement arguments, string button, int duration, GestureContext context)
    {
        DragPoints points = ResolveDragPoints(arguments, context.Display);
        int clampedDuration = Math.Clamp(duration, 20, 5000);

        int dragLeft = Math.Min(points.FromX, points.ToX) - 80;
        int dragTop = Math.Min(points.FromY, points.ToY) - 80;
        int dragWidth = Math.Abs(points.ToX - points.FromX) + 160;
        int dragHeight = Math.Abs(points.ToY - points.FromY) + 160;

        ObserveSignature? dragBefore = context.Observe
            ? _host._display.CaptureSignature(context.Display, dragLeft, dragTop, dragWidth, dragHeight)
            : null;

        string? detail = InjectDrag(context.UseInput, points, button, clampedDuration);

        double injectMs = context.Stopwatch.Elapsed.TotalMilliseconds;
        Thread.Sleep(50);

        if (WindowManager.GetForegroundWindow() != context.PreviousForeground)
        {
            _host.RestoreFocusAfterClick(context.PreviousForeground);
        }

        GestureView dragResult = BuildDragView(context.UseInput, points, button, detail, injectMs, context.Stopwatch);

        ObserveSignature? dragAfter = dragBefore is null
            ? null
            : _host._display.CaptureSignature(context.Display, dragLeft, dragTop, dragWidth, dragHeight);
        ObservedView? dragObserved = _host._uia.BuildObserved(
            dragBefore, dragAfter, context.Stopwatch.Elapsed.TotalMilliseconds);

        if (dragObserved is not null)
        {
            dragResult = dragResult with { Observed = dragObserved, Verified = dragObserved.Changed };
        }

        return dragResult;
    }

    private DragPoints ResolveDragPoints(JsonElement arguments, DisplayInfo display)
    {
        (int fromX, int fromY) = _host.GetPoint(arguments, "from")
            ?? throw new ToolException("E_INVALID_ARG", "drag 需要 from");
        (int toX, int toY) = _host.GetPoint(arguments, "to")
            ?? throw new ToolException("E_INVALID_ARG", "drag 需要 to");

        _host._element.RequirePointInTrackedApp(display, fromX, fromY, "drag.from");
        _host._element.RequirePointInTrackedApp(display, toX, toY, "drag.to");

        return new DragPoints(fromX, fromY, toX, toY);
    }

    private string? InjectDrag(bool useInput, DragPoints points, string button, int duration)
    {
        if (useInput)
        {
            _host._focus.PauseCursor();
            _host._focus.PauseFocusGuard(0);

            try
            {
                InputInjector.Drag(points.FromX, points.FromY, points.ToX, points.ToY, button, duration);
            }
            finally
            {
                _host._focus.ResumeCursor();
                _host._focus.ResumeFocusGuard();
            }

            return null;
        }

        if (MessageInput.Drag(
                new DragRequest(points.FromX, points.FromY, points.ToX, points.ToY, button, duration),
                out string? detail))
        {
            return detail;
        }

        throw new ToolException(
            "E_INPUT_UNSUPPORTED",
            "窗口消息拖拽失败；如接受光标跳变，请显式使用 mode=\"input\"",
            new JsonObject { ["detail"] = detail });
    }

    private static GestureView BuildDragView(
        bool useInput, DragPoints points, string button, string? detail, double injectMs, Stopwatch stopwatch)
    {
        var view = new GestureView(
            "drag",
            new PointView(points.FromX, points.FromY),
            new PointView(points.ToX, points.ToY),
            null,
            null,
            button,
            useInput ? "input" : "message",
            detail,
            false,
            Math.Round(injectMs, 2),
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            null,
            null);

        if (useInput)
        {
            return view;
        }

        return view with
        {
            Hint = "窗口消息拖拽可能被应用忽略；如无效请改用 mode=input，并用截图/断言验证",
        };
    }

    internal GestureView GestureScroll(JsonElement arguments, GestureContext context)
    {
        DisplayInfo display = context.Display;
        bool observe = context.Observe;
        bool useInput = context.UseInput;
        Stopwatch stopwatch = context.Stopwatch;
        IntPtr previousForeground = context.PreviousForeground;
        (int x, int y) = ResolveScrollPoint(arguments);

        int dx = GetInt(arguments, "dx", 0);
        int dy = GetInt(arguments, "dy", 0);

        if (dx == 0 && dy == 0)
        {
            throw new ToolException("E_INVALID_ARG", "scroll 需要 dx 或 dy");
        }

        _host._element.RequirePointInTrackedApp(display, x, y, "scroll");

        ObserveSignature? scrollBefore = observe ? CaptureScrollArea(display, x, y) : null;
        string? detail = InjectScroll(useInput, x, y, dx, dy);

        double injectMs = stopwatch.Elapsed.TotalMilliseconds;
        Thread.Sleep(50);

        if (WindowManager.GetForegroundWindow() != previousForeground)
        {
            _host.RestoreFocusAfterClick(previousForeground);
        }

        GestureView scrollResult = BuildScrollView(useInput, dx, dy, detail, injectMs, stopwatch);

        ObserveSignature? scrollAfter = scrollBefore is null ? null : CaptureScrollArea(display, x, y);
        ObservedView? scrollObserved = _host._uia.BuildObserved(
            scrollBefore, scrollAfter, stopwatch.Elapsed.TotalMilliseconds);

        if (scrollObserved is not null)
        {
            scrollResult = scrollResult with { Observed = scrollObserved, Verified = scrollObserved.Changed };
        }

        return scrollResult;
    }

    private (int X, int Y) ResolveScrollPoint(JsonElement arguments)
    {
        if (_host.GetPoint(arguments, "from") is { } point)
        {
            return (point.X, point.Y);
        }

        TrackedApp app = _host.RequireTrackedApp();
        WindowInfo info = WindowManager.FromHandle(app.Hwnd)
            ?? throw new ToolException("E_NO_TARGET", "目标窗口已失效");

        return (info.Rect.Left + info.Width / 2, info.Rect.Top + info.Height / 2);
    }

    private ObserveSignature? CaptureScrollArea(DisplayInfo display, int x, int y) =>
        _host._display.CaptureSignature(display, x - 120, y - 120, 240, 240);

    private string? InjectScroll(bool useInput, int x, int y, int dx, int dy)
    {
        if (useInput)
        {
            _host._focus.PauseCursor();
            _host._focus.PauseFocusGuard(0);

            try
            {
                InputInjector.Scroll(x, y, dx, dy);
            }
            finally
            {
                _host._focus.ResumeCursor();
                _host._focus.ResumeFocusGuard();
            }

            return null;
        }

        if (MessageInput.Scroll(x, y, dx, dy, out string? detail))
        {
            return detail;
        }

        throw new ToolException(
            "E_INPUT_UNSUPPORTED",
            "窗口消息滚轮失败；如接受光标跳变，请显式使用 mode=\"input\"",
            new JsonObject { ["detail"] = detail });
    }

    private static GestureView BuildScrollView(
        bool useInput, int dx, int dy, string? detail, double injectMs, Stopwatch stopwatch)
    {
        var view = new GestureView(
            "scroll",
            null,
            null,
            dx,
            dy,
            null,
            useInput ? "input" : "message",
            detail,
            false,
            Math.Round(injectMs, 2),
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            null,
            null);

        if (useInput)
        {
            return view;
        }

        return view with
        {
            Hint = "窗口消息滚轮可能被未聚焦的应用忽略，请用 assert_region/wait_for 验证",
        };
    }

    internal GestureView GestureMove(JsonElement arguments, GestureContext context)
    {
        DisplayInfo display = context.Display;
        bool useInput = context.UseInput;
        Stopwatch stopwatch = context.Stopwatch;
        IntPtr previousForeground = context.PreviousForeground;
        (int toX, int toY) = _host.GetPoint(arguments, "to")
            ?? throw new ToolException("E_INVALID_ARG", "move 需要 to");

        _host._element.RequirePointInTrackedApp(display, toX, toY, "move.to");
        string? detail = null;

        if (useInput)
        {
            _host._focus.PauseCursor();
            _host._focus.PauseFocusGuard(0);
            try
            {
                InputInjector.Move(toX, toY);
            }
            finally
            {
                _host._focus.ResumeCursor();
                _host._focus.ResumeFocusGuard();
            }
        }
        else if (!MessageInput.Move(toX, toY, out detail))
        {
            throw new ToolException(
                "E_INPUT_UNSUPPORTED",
                "窗口消息移动失败；如接受光标跳变，请显式使用 mode=\"input\"",
                new JsonObject { ["detail"] = detail });
        }

        double injectMs = stopwatch.Elapsed.TotalMilliseconds;
        Thread.Sleep(50);
        if (WindowManager.GetForegroundWindow() != previousForeground)
        {
            _host.RestoreFocusAfterClick(previousForeground);
        }

        return new GestureView(
            "move",
            null,
            new PointView(toX, toY),
            null,
            null,
            null,
            useInput ? "input" : "message",
            detail,
            false,
            Math.Round(injectMs, 2),
            Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            null,
            null);
    }
}
