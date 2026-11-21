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
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using static VScreenPilot.Json.JsonArgReader;
using ObserveSignature = VScreenPilot.Verification.ObserveSampler.Signature;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

internal sealed partial class InputRouter
{
    public GestureView Gesture(JsonElement arguments)
    {
        DisplayInfo display = _host.Ensure();

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

        if (useInput && !_host.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }

        return type.ToLowerInvariant() switch
        {
            "drag" => GestureDrag(arguments, display, button, duration, observe, useInput, stopwatch, previousForeground),
            "scroll" => GestureScroll(arguments, display, observe, useInput, stopwatch, previousForeground),
            "move" => GestureMove(arguments, display, useInput, stopwatch, previousForeground),
            _ => throw new ToolException("E_INVALID_ARG", $"未知 gesture 类型: {type}"),
        };
    }

    private GestureView GestureDrag(
        JsonElement arguments, DisplayInfo display, string button, int duration, bool observe,
        bool useInput, Stopwatch stopwatch, IntPtr previousForeground)
    {
                (int fromX, int fromY) = GetPoint(arguments, "from")
                    ?? throw new ToolException("E_INVALID_ARG", "drag 需要 from");
                (int toX, int toY) = GetPoint(arguments, "to")
                    ?? throw new ToolException("E_INVALID_ARG", "drag 需要 to");

                int clampedDuration = Math.Clamp(duration, 20, 5000);
                _host.RequirePointInTrackedApp(display, fromX, fromY, "drag.from");
                _host.RequirePointInTrackedApp(display, toX, toY, "drag.to");

                int dragLeft = Math.Min(fromX, toX) - 80;
                int dragTop = Math.Min(fromY, toY) - 80;
                int dragWidth = Math.Abs(toX - fromX) + 160;
                int dragHeight = Math.Abs(toY - fromY) + 160;
                ObserveSignature? dragBefore = observe
                    ? _host.CaptureSignature(display, dragLeft, dragTop, dragWidth, dragHeight)
                    : null;

                string? detail = null;

                if (useInput)
                {
                    _host.PauseCursor();
                    _host.PauseFocusGuard();
                    try
                    {
                        InputInjector.Drag(fromX, fromY, toX, toY, button, clampedDuration);
                    }
                    finally
                    {
                        _host.ResumeCursor();
                        _host.ResumeFocusGuard();
                    }
                }
                else if (!MessageInput.Drag(
                             fromX, fromY, toX, toY, button, clampedDuration, out detail))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "窗口消息拖拽失败；如接受光标跳变，请显式使用 mode=\"input\"",
                        new JsonObject { ["detail"] = detail });
                }

                double injectMs = stopwatch.Elapsed.TotalMilliseconds;
                Thread.Sleep(50);
                if (WindowManager.GetForegroundWindow() != previousForeground)
                {
                    RestoreFocusAfterClick(previousForeground);
                }

                var dragResult = new GestureView(
                    "drag",
                    new PointView(fromX, fromY),
                    new PointView(toX, toY),
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

                if (!useInput)
                {
                    dragResult = dragResult with
                    {
                        Hint = "窗口消息拖拽可能被应用忽略；如无效请改用 mode=input，并用截图/断言验证",
                    };
                }

                ObserveSignature? dragAfter = dragBefore is null
                    ? null
                    : _host.CaptureSignature(display, dragLeft, dragTop, dragWidth, dragHeight);
                ObservedView? dragObserved = _host.BuildObserved(
                    dragBefore, dragAfter, stopwatch.Elapsed.TotalMilliseconds);

                if (dragObserved is not null)
                {
                    dragResult = dragResult with { Observed = dragObserved, Verified = dragObserved.Changed };
                    
                }

                return dragResult;
    }

    private GestureView GestureScroll(
        JsonElement arguments, DisplayInfo display, bool observe,
        bool useInput, Stopwatch stopwatch, IntPtr previousForeground)
    {
                (int? x, int? y) = GetPoint(arguments, "from") is { } point
                    ? (point.X, point.Y)
                    : ((int?)null, (int?)null);

                int dx = GetInt(arguments, "dx", 0);
                int dy = GetInt(arguments, "dy", 0);

                if (dx == 0 && dy == 0)
                {
                    throw new ToolException("E_INVALID_ARG", "scroll 需要 dx 或 dy");
                }

                string? detail = null;

                if (x is null || y is null)
                {
                    TrackedApp app = RequireTrackedApp();
                    WindowInfo? info = WindowManager.FromHandle(app.Hwnd);
                    if (info is null)
                    {
                        throw new ToolException("E_NO_TARGET", "目标窗口已失效");
                    }

                    x = info.Rect.Left + info.Width / 2;
                    y = info.Rect.Top + info.Height / 2;
                }

                _host.RequirePointInTrackedApp(display, x.Value, y.Value, "scroll");

                ObserveSignature? scrollBefore = observe
                    ? _host.CaptureSignature(display, x.Value - 120, y.Value - 120, 240, 240)
                    : null;

                if (useInput)
                {
                    _host.PauseCursor();
                    _host.PauseFocusGuard();
                    try
                    {
                        InputInjector.Scroll(x, y, dx, dy);
                    }
                    finally
                    {
                        _host.ResumeCursor();
                        _host.ResumeFocusGuard();
                    }
                }
                else
                {
                    if (!MessageInput.Scroll(x.Value, y.Value, dx, dy, out detail))
                    {
                        throw new ToolException(
                            "E_INPUT_UNSUPPORTED",
                            "窗口消息滚轮失败；如接受光标跳变，请显式使用 mode=\"input\"",
                            new JsonObject { ["detail"] = detail });
                    }
                }

                double injectMs = stopwatch.Elapsed.TotalMilliseconds;
                Thread.Sleep(50);
                if (WindowManager.GetForegroundWindow() != previousForeground)
                {
                    RestoreFocusAfterClick(previousForeground);
                }

                var scrollResult = new GestureView(
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

                if (!useInput)
                {
                    scrollResult = scrollResult with
                    {
                        Hint = "窗口消息滚轮可能被未聚焦的应用忽略，请用 assert_region/wait_for 验证",
                    };
                }

                ObserveSignature? scrollAfter = scrollBefore is null
                    ? null
                    : _host.CaptureSignature(display, x.Value - 120, y.Value - 120, 240, 240);
                ObservedView? scrollObserved = _host.BuildObserved(
                    scrollBefore, scrollAfter, stopwatch.Elapsed.TotalMilliseconds);

                if (scrollObserved is not null)
                {
                    scrollResult = scrollResult with { Observed = scrollObserved, Verified = scrollObserved.Changed };
                    
                }

                return scrollResult;
    }

    private GestureView GestureMove(
        JsonElement arguments, DisplayInfo display, bool useInput,
        Stopwatch stopwatch, IntPtr previousForeground)
    {
                (int toX, int toY) = GetPoint(arguments, "to")
                    ?? throw new ToolException("E_INVALID_ARG", "move 需要 to");

                _host.RequirePointInTrackedApp(display, toX, toY, "move.to");
                string? detail = null;

                if (useInput)
                {
                    _host.PauseCursor();
                    _host.PauseFocusGuard();
                    try
                    {
                        InputInjector.Move(toX, toY);
                    }
                    finally
                    {
                        _host.ResumeCursor();
                        _host.ResumeFocusGuard();
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
                    RestoreFocusAfterClick(previousForeground);
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
