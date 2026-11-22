using System.Text.Json;
using VScreenPilot.Apps;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Views;
using VScreenPilot.Platform.Displays;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>InputOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionInputOps
{
    internal readonly SessionManager _host;

    public SessionInputOps(SessionManager host) => _host = host;
    public ClickRefView ClickElementRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput = false) =>
        _host._input.ClickOps.ClickElementRef(reference, channel, keepForeground, fallbackInput);

    public ClickView Click(SessionClickRequest request) => _host._input.ClickOps.Click(request);

    public TypeTextView TypeText(
        string text, int? pid = null, bool verify = true, int chunkChars = 32, string? channel = null) =>
        _host._input.KeyboardOps.TypeText(text, pid, verify, chunkChars, channel);

    public PressKeysView PressKeys(
        string keys, bool keepForeground = false, int? pid = null,
        bool allowSystemKeys = false, bool observe = true) =>
        _host._input.KeyboardOps.PressKeys(keys, keepForeground, pid, allowSystemKeys, observe);

    public PressKeysView PressKeySequence(
        IReadOnlyList<string> keys,
        bool keepForeground = false,
        int? pid = null,
        bool allowSystemKeys = false,
        bool observe = true) =>
        _host._input.KeyboardOps.PressKeySequence(keys, keepForeground, pid, allowSystemKeys, observe);

    public GestureView Gesture(JsonElement arguments) => _host._input.GestureOps.Gesture(arguments);

    internal bool EnsureForeground(TrackedApp app)
    {
        if (IsAppForeground(app))
        {
            return true;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            // 模态框会比主窗口更“活跃”，优先激活当前弹出窗口。
            IntPtr target = WindowManager.GetActivePopup(app.Hwnd);
            WindowManager.ForceForeground(target);

            if (!IsAppForeground(app))
            {
                WindowManager.SwitchTo(target);
            }

            if (IsAppForeground(app))
            {
                return true;
            }

            Thread.Sleep(80);
        }

        return IsAppForeground(app);
    }

    internal bool IsAppForeground(TrackedApp app) =>
        app.Pid > 0 &&
        WindowManager.GetProcessId(WindowManager.GetForegroundWindow()) == app.Pid;

    /// <summary>
    /// 判断点是否落在顶层窗口的非客户区（标题栏/边框/系统按钮）。
    /// 窗口消息通道只能投递 client 区消息，打不到非客户区；必须显式拒绝而不是报“成功”。
    /// </summary>

    internal void RestoreForeground(IntPtr previous, bool logFailure = true)
    {
        if (previous == IntPtr.Zero || !NativeMethods.IsWindow(previous))
        {
            return;
        }

        IntPtr currentForeground = NativeMethods.GetForegroundWindow();
        if (currentForeground == previous)
        {
            return;
        }

        // 用户在工具执行期间主动切到了别的窗口：不要为了“归还前台”再把用户抢走。
        // 只有当前前台仍是本会话应用（说明是应用自己抢了前台）时才恢复。
        if (!_host.FocusOps.IsTrackedWindow(currentForeground))
        {
            return;
        }

        // 目标线程挂起时 AttachThreadInput 会把服务端拖死；先做 120ms 健康检查。
        if (NativeMethods.SendMessageTimeoutW(
                previous, NativeMethods.WmNull, IntPtr.Zero, IntPtr.Zero,
                NativeMethods.SmtoAbortIfHung, 120, out _) == IntPtr.Zero)
        {
            return;
        }

        uint currentThread = NativeMethods.GetCurrentThreadId();
        uint foregroundThread = currentForeground == IntPtr.Zero
            ? 0
            : NativeMethods.GetWindowThreadProcessId(currentForeground, out _);
        uint targetThread = NativeMethods.GetWindowThreadProcessId(previous, out _);

        var attached = new List<uint>();

        try
        {
            // 标准做法：同时附加到“当前前台线程”和“目标线程”，才能绕过前台锁。
            if (foregroundThread != 0 && foregroundThread != currentThread)
            {
                if (NativeMethods.AttachThreadInput(currentThread, foregroundThread, true))
                {
                    attached.Add(foregroundThread);
                }
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread)
            {
                if (NativeMethods.AttachThreadInput(currentThread, targetThread, true))
                {
                    attached.Add(targetThread);
                }
            }

            if (!NativeMethods.SetForegroundWindow(previous))
            {
                if (logFailure)
                {
                    _host._log.Warn($"恢复前台窗口失败 hwnd=0x{previous.ToInt64():X}");
                }
            }
            else if (logFailure && NativeMethods.GetForegroundWindow() != previous)
            {
                _host._log.Warn(
                    $"恢复前台未生效 expected=0x{previous.ToInt64():X} " +
                    $"actual=0x{NativeMethods.GetForegroundWindow().ToInt64():X}");
            }
        }
        finally
        {
            foreach (uint thread in attached)
            {
                NativeMethods.AttachThreadInput(currentThread, thread, false);
            }
        }
    }

    internal TrackedApp RequirePointInTrackedApp(DisplayInfo display, int x, int y, string label)
    {
        RequirePointInDisplay(display, x, y, label);

        IntPtr target = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = x, Y = y });
        if (target == IntPtr.Zero)
        {
            throw new ToolException("E_NO_TARGET", $"{label} 位置没有可操作窗口");
        }

        IntPtr root = NativeMethods.GetAncestor(target, NativeMethods.GaRoot);
        if (root == IntPtr.Zero)
        {
            root = target;
        }

        _ = NativeMethods.GetWindowThreadProcessId(target, out uint targetPid);
        _ = NativeMethods.GetWindowThreadProcessId(root, out uint rootPid);

        TrackedApp? tracked;
        lock (_host._gate)
        {
            // 命中窗口或其顶层根窗口属于本会话应用即可（兼容 UWP/宿主窗口）。
            // 同时校验窗口仍属于登记时的进程，避免 PID 复用后误判为可操作目标。
            tracked = _host._apps.FirstOrDefault(
                app => _host.ClientOps.IsOwned(app) &&
                       (app.Pid == (int)targetPid || app.Pid == (int)rootPid) &&
                       TrackedAppTracker.IsLive(app));
        }

        if (tracked is not null)
        {
            return tracked;
        }

        WindowInfo? info = WindowManager.FromHandle(root);
        throw new ToolException(
            "E_FOREIGN_WINDOW",
            $"{label} 落在非本会话窗口，拒绝操作以免影响用户桌面",
            new JsonObject
            {
                ["hwnd"] = root.ToInt64(),
                ["pid"] = (int)rootPid,
                ["title"] = info?.Title ?? string.Empty,
            });
    }

    internal void RequireWindowOnDisplay(DisplayInfo display, IntPtr hwnd, string label)
    {
        WindowInfo? info = WindowManager.FromHandle(hwnd);
        if (info is null)
        {
            throw new ToolException("E_NO_TARGET", "目标窗口已失效");
        }

        long centerX = info.Rect.Left + info.Width / 2L;
        long centerY = info.Rect.Top + info.Height / 2L;

        if (centerX < display.X || centerX >= (long)display.X + display.Width ||
            centerY < display.Y || centerY >= (long)display.Y + display.Height)
        {
            throw new ToolException(
                "E_TARGET_OFFSCREEN",
                $"{label} 的目标窗口已不在虚拟屏内，请重新调用 launch_or_place_app",
                new JsonObject
                {
                    ["hwnd"] = hwnd.ToInt64(),
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = info.Rect.Left,
                        ["y"] = info.Rect.Top,
                        ["width"] = info.Width,
                        ["height"] = info.Height,
                    },
                    ["display"] = new JsonObject
                    {
                        ["x"] = display.X,
                        ["y"] = display.Y,
                        ["width"] = display.Width,
                        ["height"] = display.Height,
                    },
                });
        }
    }

    internal void RequirePointInDisplay(DisplayInfo display, int x, int y, string label)
    {
        if (x < display.X || x >= display.X + display.Width ||
            y < display.Y || y >= display.Y + display.Height)
        {
            throw new ToolException(
                "E_OUT_OF_BOUNDS",
                $"{label} 不在虚拟屏范围内，拒绝操作主屏",
                new JsonObject
                {
                    ["x"] = x,
                    ["y"] = y,
                    ["display"] = new JsonObject
                    {
                        ["x"] = display.X,
                        ["y"] = display.Y,
                        ["width"] = display.Width,
                        ["height"] = display.Height,
                    },
                });
        }
    }
}
