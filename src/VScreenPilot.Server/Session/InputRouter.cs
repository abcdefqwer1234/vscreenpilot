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
    Action ForceResumeFocusGuard,
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
    internal readonly ForegroundLease Lease = new();
    internal readonly object _blockGate = new();
    internal readonly Dictionary<string, HashSet<int>> _uiaBlockedPids = new(StringComparer.Ordinal);

    private readonly object _exclusiveGate = new();
    private Thread? _exclusiveThread;
    private volatile bool _exclusiveRunning;
    private string _exclusiveClientId = string.Empty;
    private int _exclusivePid;
    private IntPtr _exclusiveHwnd;

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

        IntPtr previous = Lease.ClearAll();
        StopExclusiveGuard();

        // forceAllClients/daemon 退出时也要把最早的前台归还给用户，而不是只清租约。
        if (previous != IntPtr.Zero)
        {
            _focus.RestoreForeground(previous);
        }

        _focus.ForceResumeFocusGuard();
    }

    /// <summary>当前客户端的输入/前台租约；没有则 null。</summary>
    internal InputLeaseView? CurrentLease => Lease.Status(_session.Clients.CurrentId);

    /// <summary>是否有任意客户端持有前台租约；CursorGuard 的后台线程用。</summary>
    internal bool AnyLeaseActive => Lease.IsAnyActive();

    public void UnblockClient(string clientId)
    {
        lock (_blockGate)
        {
            _uiaBlockedPids.Remove(clientId);
        }

        IntPtr previous = Lease.OnClientDrop(clientId);

        if (string.Equals(_exclusiveClientId, clientId, StringComparison.Ordinal))
        {
            StopExclusiveGuard();
        }

        RestoreForegroundWhenIdle(previous);

        // 只有最后一个租约被移除时才强制恢复；其他客户端仍在租约内时必须保持暂停。
        if (!Lease.IsAnyActive())
        {
            _focus.ForceResumeFocusGuard();
        }
    }

    /// <summary>
    /// 归还前台前先确认没有其他活跃租约：仍有客户端持有租约时只暂存目标，
    /// 等最后一个租约释放/断开后再恢复，避免把对方正在保持的前台抢回用户窗口。
    /// </summary>
    private bool RestoreForegroundWhenIdle(IntPtr previous)
    {
        if (Lease.IsAnyActive())
        {
            Lease.DeferRestore(previous);
            return false;
        }

        IntPtr target = Lease.TakeRestoreTarget(previous);

        if (target == IntPtr.Zero)
        {
            return false;
        }

        _focus.RestoreForeground(target);
        return true;
    }

    /// <summary>lease_input：acquire/release/renew/status。</summary>
    public InputLeaseView LeaseInput(string action, int? pid, int ttlMs, bool exclusive, bool force)
    {
        string clientId = _session.Clients.CurrentId;
        string mode = (action ?? "status").Trim().ToLowerInvariant();

        switch (mode)
        {
            case "acquire":
                {
                    TrackedApp app = RequireTrackedApp(pid);
                    IntPtr previous = WindowManager.GetForegroundWindow();
                    InputLeaseView view = Lease.Acquire(
                        clientId, app.Pid, app.Hwnd.ToInt64(), ttlMs, exclusive, "lease_input", previous, force);
                    _focus.PauseFocusGuard(
                        Math.Clamp(ttlMs <= 0 ? ForegroundLease.DefaultTtlMs : ttlMs,
                            1000, ForegroundLease.MaxTtlMs));
                    UpdateExclusiveGuard(clientId, app.Pid, app.Hwnd, exclusive);
                    return view;
                }

            case "release":
                {
                    (InputLeaseView? view, IntPtr previous) = Lease.Release(clientId);

                    if (string.Equals(_exclusiveClientId, clientId, StringComparison.Ordinal))
                    {
                        StopExclusiveGuard();
                    }

                    bool restored = RestoreForegroundWhenIdle(previous);

                    // 显式 release 后，没有其他租约才能强制恢复；否则保留对方的暂停。
                    if (Lease.IsAnyActive())
                    {
                        _focus.ResumeFocusGuard();
                    }
                    else if (view is not null || previous != IntPtr.Zero || restored)
                    {
                        _focus.ForceResumeFocusGuard();
                    }

                    return view ?? new InputLeaseView(string.Empty, 0, 0, 0, false, false, "none");
                }

            case "renew":
                {
                    InputLeaseView? current = Lease.Status(clientId);
                    bool effectiveExclusive = exclusive || current?.Exclusive == true;
                    InputLeaseView? view = Lease.Renew(clientId, ttlMs, effectiveExclusive);

                    if (view is not null)
                    {
                        _focus.PauseFocusGuard(
                            Math.Clamp(ttlMs <= 0 ? ForegroundLease.DefaultTtlMs : ttlMs,
                                1000, ForegroundLease.MaxTtlMs));
                        UpdateExclusiveGuard(clientId, view.Pid, new IntPtr(view.Hwnd), effectiveExclusive);
                    }

                    return view ?? new InputLeaseView(string.Empty, 0, 0, 0, false, false, "none");
                }

            case "status":
                return Lease.Status(clientId)
                    ?? new InputLeaseView(string.Empty, 0, 0, 0, false, false, "none");

            default:
                throw new ToolException(
                    "E_INVALID_ARG",
                    $"lease_input action 只能是 acquire/release/renew/status（收到 {action}）",
                    retryable: false,
                    hint: "acquire 后输入调用共享前台；release 归还",
                    stage: "validate");
        }
    }

    private void UpdateExclusiveGuard(string clientId, int pid, IntPtr hwnd, bool exclusive)
    {
        if (!exclusive)
        {
            // 非独占 renew 不能停掉另一个客户端的独占守护。
            if (string.Equals(_exclusiveClientId, clientId, StringComparison.Ordinal))
            {
                StopExclusiveGuard();
            }

            return;
        }

        StopExclusiveGuard();

        lock (_exclusiveGate)
        {
            _exclusiveClientId = clientId;
            _exclusivePid = pid;
            _exclusiveHwnd = hwnd;
            _exclusiveRunning = true;
            _exclusiveThread = new Thread(() => ExclusiveLoop(clientId, pid, hwnd))
            {
                IsBackground = true,
                Name = "InputLeaseExclusive",
                Priority = ThreadPriority.BelowNormal,
            };
            _exclusiveThread.Start();
        }
    }

    private void ExclusiveLoop(string clientId, int pid, IntPtr hwnd)
    {
        while (_exclusiveRunning)
        {
            Thread.Sleep(200);

            if (!_exclusiveRunning ||
                !string.Equals(_exclusiveClientId, clientId, StringComparison.Ordinal) ||
                _exclusivePid != pid ||
                _exclusiveHwnd != hwnd)
            {
                return;
            }

            if (!Lease.IsActive(clientId))
            {
                return;
            }

            IntPtr foreground = WindowManager.GetForegroundWindow();

            if (foreground != IntPtr.Zero &&
                foreground != hwnd &&
                WindowManager.GetProcessId(foreground) != pid)
            {
                WindowManager.ForceForeground(hwnd);
            }
        }
    }

    private void StopExclusiveGuard()
    {
        Thread? thread;

        lock (_exclusiveGate)
        {
            _exclusiveRunning = false;
            thread = _exclusiveThread;
            _exclusiveThread = null;
            _exclusiveClientId = string.Empty;
            _exclusivePid = 0;
            _exclusiveHwnd = IntPtr.Zero;
        }

        thread?.Join(300);
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

        if (Lease.IsActive(_session.Clients.CurrentId))
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

        ElevationGuard.RequireNotElevated(app.Pid, "输入");
        return app;
    }

    /// <summary>
    /// 解析本次输入的目标窗口：默认是受管应用主窗口；hwnd 可指定同一应用下的对话框。
    /// 目标必须属于受管进程，避免把键盘输入发到用户的其他窗口。
    /// </summary>
    internal (TrackedApp App, IntPtr Hwnd) RequireInputTarget(
        int? pid, long? hwnd, string? target = null)
    {
        string targetMode = (target ?? "app").Trim().ToLowerInvariant();

        if (targetMode is not ("app" or "current_dialog" or "foreground"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知输入目标: {target}（可选 app/current_dialog/foreground）",
                new JsonObject { ["target"] = target });
        }

        if (targetMode == "current_dialog")
        {
            if (hwnd is { } explicitHwnd && explicitHwnd > 0)
            {
                throw new ToolException(
                    "E_INVALID_ARG",
                    "hwnd 与 target=current_dialog 不能同时使用",
                    new JsonObject { ["hwnd"] = explicitHwnd, ["target"] = target });
            }

            TrackedApp targetApp = RequireTrackedApp(pid);
            return (targetApp, ResolveCurrentDialog(targetApp));
        }

        if (targetMode == "foreground")
        {
            TrackedApp foregroundApp = RequireTrackedApp(pid);
            IntPtr foreground = WindowManager.GetForegroundWindow();

            if (foreground == IntPtr.Zero || WindowManager.GetProcessId(foreground) != foregroundApp.Pid)
            {
                throw new ToolException(
                    "E_FOCUS",
                    "target=foreground 要求目标应用当前在前台",
                    new JsonObject { ["pid"] = foregroundApp.Pid },
                    retryable: true,
                    hint: "先 click(keep_foreground=true) 激活应用，或改用 target=app/current_dialog",
                    stage: "focus");
            }

            return (foregroundApp, foreground);
        }

        if (hwnd is null || hwnd.Value <= 0)
        {
            TrackedApp fallback = RequireTrackedApp(pid);
            return (fallback, fallback.Hwnd);
        }

        IntPtr explicitTarget = new(hwnd.Value);

        if (!NativeMethods.IsWindow(explicitTarget))
        {
            throw new ToolException(
                "E_WINDOW_NOT_FOUND",
                $"hwnd=0x{hwnd.Value:X} 不是有效窗口",
                new JsonObject { ["hwnd"] = hwnd.Value });
        }

        int targetPid = WindowManager.GetProcessId(explicitTarget);

        if (pid is not null && pid.Value != targetPid)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"pid={pid} 与 hwnd=0x{hwnd.Value:X} 所属进程 {targetPid} 不一致",
                new JsonObject { ["pid"] = pid, ["hwnd"] = hwnd.Value, ["hwnd_pid"] = targetPid });
        }

        TrackedApp app = RequireTrackedApp(targetPid);
        return (app, explicitTarget);
    }

    /// <summary>目标窗口是否属于 app 主窗口的 owner 链（真正的模态/弹出窗）。</summary>
    private static bool IsOwnedByApp(IntPtr window, TrackedApp app) =>
        WindowManager.ListOwnedWindows(app.Pid, app.Hwnd)
            .Any(owned => owned.Hwnd == window);

    /// <summary>
    /// 解析“当前模态对话框”：活动弹窗 → 前台同进程 owned 窗口 → 唯一 enabled 的 owned 顶层窗口。
    /// 多个同权候选时返回 E_WINDOW_AMBIGUOUS，避免猜错目标。
    /// </summary>
    private IntPtr ResolveCurrentDialog(TrackedApp app)
    {
        IntPtr popup = WindowManager.GetActivePopup(app.Hwnd);

        if (popup != app.Hwnd && NativeMethods.IsWindowVisible(popup))
        {
            return popup;
        }

        IntPtr foreground = WindowManager.GetForegroundWindow();

        if (foreground != IntPtr.Zero &&
            foreground != app.Hwnd &&
            WindowManager.GetProcessId(foreground) == app.Pid &&
            NativeMethods.IsWindowVisible(foreground) &&
            IsOwnedByApp(foreground, app))
        {
            return foreground;
        }

        var candidates = new List<WindowInfo>();
        var seen = new HashSet<IntPtr>();

        foreach (WindowInfo window in WindowManager.ListOwnedWindows(app.Pid, app.Hwnd))
        {
            if (!window.Visible ||
                !NativeMethods.IsWindowEnabled(window.Hwnd) ||
                window.Area <= 0 ||
                string.Equals(window.ClassName, "#32768", StringComparison.Ordinal) ||
                !seen.Add(window.Hwnd))
            {
                continue;
            }

            candidates.Add(window);
        }

        if (candidates.Count == 1)
        {
            return candidates[0].Hwnd;
        }

        if (candidates.Count == 0)
        {
            throw new ToolException(
                "E_WINDOW_NOT_FOUND",
                "当前没有可操作的模态对话框",
                new JsonObject { ["pid"] = app.Pid, ["hwnd"] = app.Hwnd.ToInt64() },
                retryable: true,
                hint: "先打开对话框；或用 list_windows/E_BLOCKED details 取具体 hwnd",
                stage: "dialog");
        }

        var details = new JsonArray();

        foreach (WindowInfo window in candidates.Take(20))
        {
            details.Add((JsonNode)new JsonObject
            {
                ["hwnd"] = window.Hwnd.ToInt64(),
                ["title"] = window.Title,
                ["class"] = window.ClassName,
                ["enabled"] = NativeMethods.IsWindowEnabled(window.Hwnd),
            });
        }

        throw new ToolException(
            "E_WINDOW_AMBIGUOUS",
            "存在多个候选对话框，无法确定 target=current_dialog 的目标",
            new JsonObject
            {
                ["pid"] = app.Pid,
                ["candidates"] = details,
            },
            retryable: true,
            hint: "用 hwnd 指定具体窗口，或先让目标对话框获得前台",
            stage: "dialog");
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
