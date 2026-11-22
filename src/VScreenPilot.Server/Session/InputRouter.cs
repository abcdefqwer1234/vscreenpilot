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

internal sealed record TextWriteResult(
    int Typed,
    int Chunks,
    bool TextSupported,
    bool Verified,
    int? ObservedLength,
    double InjectMs,
    double DurationMs);

/// <summary>输入路由所需的最小宿主接口。</summary>
internal delegate UiaElementHandle ResolveHandleHandler(
    string reference, DisplayInfo display, out ElementRefEntry entry, out bool refreshed);

internal sealed record InputSessionContext(
    LogRing Log,
    ClientSessionRegistry Clients,
    Func<CancellationToken> Cancellation);

internal sealed record InputFocusContext(
    Func<TrackedApp, bool> EnsureForeground,
    Func<TrackedApp, bool> IsAppForeground,
    Action<IntPtr> RestoreForeground,
    Action<int> PauseFocusGuard,
    Action ResumeFocusGuard,
    Action PauseCursor,
    Action ResumeCursor);

internal sealed record InputDisplayContext(
    Func<DisplayInfo> EnsureDisplay,
    Func<DisplayInfo> Ensure,
    Action<DisplayInfo, IntPtr, string> RequireWindowOnDisplay,
    Action<DisplayInfo, int, int, string> RequirePointInDisplay,
    Func<DisplayInfo, int, int, int, int, ObserveSignature> CaptureSignature,
    Func<bool> IsInputDesktopAvailable);

internal sealed record InputElementContext(
    Func<string, ElementRefEntry> RequireElement,
    ResolveHandleHandler ResolveHandle,
    Func<uint, uint, TrackedApp?> FindTrackedAppAtWindow,
    Func<DisplayInfo, int, int, string, TrackedApp> RequirePointInTrackedApp,
    Func<TrackedApp?> LastTrackedApp,
    Func<int?, TrackedApp?> FindLiveTrackedApp);

internal sealed record InputUiaContext(
    UiaHost Uia,
    Action ResetUia,
    Func<ObserveSignature?, ObserveSignature?, double, ObservedView?> BuildObserved);
/// <summary>
/// 输入路由：坐标/ref 点击、键盘、长文本、手势；消息/输入/UIA 通道选择与前台恢复。
/// 写锁与屏幕租约由工具执行器负责，本类只做路由与动作语义。
/// </summary>
internal sealed class InputRouter
{
    internal readonly InputSessionContext _session;
    internal readonly InputFocusContext _focus;
    internal readonly InputDisplayContext _display;
    internal readonly InputElementContext _element;
    internal readonly InputUiaContext _uia;
    internal readonly LogRing _log;
    internal readonly object _blockGate = new();
    internal readonly Dictionary<string, HashSet<int>> _uiaBlockedPids = new(StringComparer.Ordinal);

    internal readonly InputClickOps ClickOps;
    internal readonly InputGestureOps GestureOps;
    internal readonly InputKeyboardOps KeyboardOps;
    internal readonly InputTextOps TextOps;
    internal readonly InputUiaGuardOps UiaGuardOps;

    public InputRouter(
        InputSessionContext session,
        InputFocusContext focus,
        InputDisplayContext display,
        InputElementContext element,
        InputUiaContext uia)
    {
        ClickOps = new InputClickOps(this);
        GestureOps = new InputGestureOps(this);
        KeyboardOps = new InputKeyboardOps(this);
        TextOps = new InputTextOps(this);
        UiaGuardOps = new InputUiaGuardOps(this);
        _session = session;
        _focus = focus;
        _display = display;
        _element = element;
        _uia = uia;
        _log = session.Log;
    }

    public void ClearBlocked()
    {
        lock (_blockGate)
        {
            _uiaBlockedPids.Clear();
        }
    }

    public void UnblockClient(string clientId)
    {
        lock (_blockGate)
        {
            _uiaBlockedPids.Remove(clientId);
        }
    }

    internal bool IsNonClientPoint(IntPtr topHwnd, int x, int y)
    {
        var point = new NativeMethods.POINT { X = x, Y = y };
        IntPtr target = NativeMethods.WindowFromPoint(point);

        if (target == IntPtr.Zero)
        {
            return true;
        }

        // 弹出菜单/owned 窗口有独立的消息泵，交给消息通道处理。
        if (NativeMethods.GetAncestor(target, NativeMethods.GaRoot) != topHwnd)
        {
            return false;
        }

        // 子控件收到的是 client 坐标消息。
        if (target != topHwnd)
        {
            return false;
        }

        IntPtr lParam = unchecked((IntPtr)((y << 16) | (x & 0xFFFF)));

        if (NativeMethods.SendMessageTimeoutW(
                topHwnd, NativeMethods.WmNcHitTest, IntPtr.Zero, lParam,
                NativeMethods.SmtoAbortIfHung, 120, out IntPtr result) == IntPtr.Zero)
        {
            return false;
        }

        // HTCLIENT = 1；0/>=2 都是非客户区。
        return result.ToInt64() != 1;
    }

    internal void RejectNonClientArea(TrackedApp app, int x, int y, string channel)
    {
        if (!IsNonClientPoint(app.Hwnd, x, y))
        {
            return;
        }

        throw new ToolException(
            "E_INPUT_UNSUPPORTED",
            $"({x},{y}) 位于窗口非客户区（标题栏/边框/系统按钮），{channel} 通道无法生效",
            new JsonObject { ["x"] = x, ["y"] = y, ["channel"] = channel },
            retryable: false,
            hint: "改用 channel=input（会移动光标），或直接用 UIA 调用最小化/关闭按钮",
            stage: "input");
    }

    /// <summary>焦点元素必须与前台窗口的客户区相交，防止同 pid 多窗口时写错文档。</summary>
    internal bool ElementBelongsToWindow(UiaElementInfo info, IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        WindowInfo? window = WindowManager.FromHandle(hwnd);
        if (window is null)
        {
            return false;
        }

        // 部分控件（如 Word 正文）上报 0 尺寸或离屏边界；此时只按 pid 放行。
        if (info.Width <= 0 || info.Height <= 0)
        {
            return true;
        }

        const int slack = 8;
        return info.Left < window.Rect.Right + slack &&
               info.Right > window.Rect.Left - slack &&
               info.Top < window.Rect.Bottom + slack &&
               info.Bottom > window.Rect.Top - slack;
    }

    internal void RestoreFocusAfterClick(IntPtr previous)
    {
        if (previous == IntPtr.Zero)
        {
            return;
        }

        IntPtr current = WindowManager.GetForegroundWindow();
        if (current == IntPtr.Zero || current == previous)
        {
            return;
        }

        TrackedApp? app = _element.LastTrackedApp();
        WindowInfo? info = WindowManager.FromHandle(current);

        // 点击可能打开了应用内的菜单/弹窗；保留其焦点，避免把刚打开的 UI 关掉。
        if (app is not null && info is not null && info.Pid == app.Pid && current != app.Hwnd)
        {
            return;
        }

        _focus.RestoreForeground(previous);
    }

    internal TrackedApp RequireTrackedApp(int? pid = null)
    {
        TrackedApp? app = _element.FindLiveTrackedApp(pid);

        if (app is null || !NativeMethods.IsWindow(app.Hwnd))
        {
            throw new ToolException(
                "E_NO_TARGET",
                pid is null
                    ? "没有已接管的应用窗口，请先调用 launch_or_place_app"
                    : $"pid={pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = pid });
        }

        return app;
    }

    internal (int X, int Y)? GetPoint(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement point) ||
            point.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        int x = GetInt(point, "x", int.MinValue);
        int y = GetInt(point, "y", int.MinValue);

        return x == int.MinValue || y == int.MinValue ? null : (x, y);
    }
}
