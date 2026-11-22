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
internal interface IInputHost
{
    LogRing Log { get; }
    ClientSessionRegistry Clients { get; }
    UiaHost Uia { get; }
    DisplayInfo EnsureDisplay();
    DisplayInfo Ensure();
    CancellationToken Cancellation { get; }
    ElementRefEntry RequireElement(string reference);
    UiaElementHandle ResolveHandle(
        string reference, DisplayInfo display, out ElementRefEntry entry, out bool refreshed);
    TrackedApp? FindTrackedAppAtWindow(uint targetPid, uint rootPid);
    TrackedApp RequirePointInTrackedApp(DisplayInfo display, int x, int y, string label);
    void RequireWindowOnDisplay(DisplayInfo display, IntPtr hwnd, string label);
    void RequirePointInDisplay(DisplayInfo display, int x, int y, string label);
    bool EnsureForeground(TrackedApp app);
    bool IsAppForeground(TrackedApp app);
    TrackedApp? LastTrackedApp();
    TrackedApp? FindLiveTrackedApp(int? pid);
    void RestoreForeground(IntPtr previous, bool logFailure = true);
    bool IsInputDesktopAvailable();
    ObserveSignature CaptureSignature(DisplayInfo display, int x, int y, int width, int height);
    ObservedView? BuildObserved(ObserveSignature? before, ObserveSignature? after, double milliseconds);
    void PauseFocusGuard(int leaseMs = 0);
    void ResumeFocusGuard();
    TextWriteResult WriteTextVerified(
        TrackedApp app, UiaElementHandle? target, string text, bool replace, int timeoutMs);
    void PauseCursor();
    void ResumeCursor();
    void ResetUia();
}

/// <summary>
/// 输入路由：坐标/ref 点击、键盘、长文本、手势；消息/输入/UIA 通道选择与前台恢复。
/// 写锁与屏幕租约由工具执行器负责，本类只做路由与动作语义。
/// </summary>
internal sealed partial class InputRouter
{
    private readonly IInputHost _host;
    private readonly LogRing _log;
    private readonly object _blockGate = new();
    private readonly Dictionary<string, HashSet<int>> _uiaBlockedPids = new(StringComparer.Ordinal);

    public InputRouter(IInputHost host)
    {
        _host = host;
        _log = host.Log;
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

    private static bool IsNonClientPoint(IntPtr topHwnd, int x, int y)
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

    private static void RejectNonClientArea(TrackedApp app, int x, int y, string channel)
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
    private static bool ElementBelongsToWindow(UiaElementInfo info, IntPtr hwnd)
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

    private void RestoreFocusAfterClick(IntPtr previous)
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

        TrackedApp? app = _host.LastTrackedApp();
        WindowInfo? info = WindowManager.FromHandle(current);

        // 点击可能打开了应用内的菜单/弹窗；保留其焦点，避免把刚打开的 UI 关掉。
        if (app is not null && info is not null && info.Pid == app.Pid && current != app.Hwnd)
        {
            return;
        }

        _host.RestoreForeground(previous);
    }

    private TrackedApp RequireTrackedApp(int? pid = null)
    {
        TrackedApp? app = _host.FindLiveTrackedApp(pid);

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

    private static (int X, int Y)? GetPoint(JsonElement element, string property)
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
