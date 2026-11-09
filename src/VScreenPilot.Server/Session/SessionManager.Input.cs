using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Interop.UIAutomationClient;
using VScreenPilot.Apps;
using VScreenPilot.Core.Displays;
using VScreenPilot.Input;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public JsonObject Click(
        int x, int y, string button, int clicks, string mode, bool keepForeground = false)
    {
        DisplayInfo display = EnsureEnvironment();

        if (clicks is < 1 or > 10)
        {
            throw new ToolException("E_INVALID_ARG", "clicks 必须在 1..10 之间");
        }

        mode = (mode ?? "auto").ToLowerInvariant();
        if (mode is not ("auto" or "uia" or "message" or "input"))
        {
            throw new ToolException("E_INVALID_ARG", $"未知 click mode: {mode}");
        }

        TrackedApp app = RequirePointInTrackedApp(display, x, y, "click");

        string method;
        string? detail = null;
        var stopwatch = Stopwatch.StartNew();
        IntPtr previousForeground = WindowManager.GetForegroundWindow();

        if (keepForeground)
        {
            // 菜单/弹出层需要连续操作时，暂时冻结焦点护栏，避免点击后前台被抢回。
            PauseFocusGuard();
        }

        bool canUseUia = button.Equals("left", StringComparison.OrdinalIgnoreCase) && clicks == 1;

        switch (mode)
        {
            case "input":
            {
                _cursorGuard?.Pause();
                PauseFocusGuard();
                try
                {
                    InputInjector.Click(x, y, button, clicks);
                }
                finally
                {
                    _cursorGuard?.Resume();

                    if (!keepForeground)
                    {
                        ResumeFocusGuard();
                    }
                }

                method = "input";
                break;
            }

            case "uia":
            {
                if (!canUseUia)
                {
                    throw new ToolException("E_INVALID_ARG", "UIA 模式仅支持左键单击");
                }

                if (!TryUiaInvoke(x, y, app.Pid))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "该坐标没有 UIA InvokePattern，无法在不移动光标的前提下点击");
                }

                method = "uia";
                break;
            }

            case "message":
            {
                if (!MessageInput.Click(x, y, button, clicks, out detail))
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "窗口消息注入失败",
                        new JsonObject { ["detail"] = detail });
                }

                method = "message";
                break;
            }

            case "auto":
            default:
            {
                if (canUseUia && TryUiaInvoke(x, y, app.Pid))
                {
                    method = "uia";
                }
                else if (MessageInput.Click(x, y, button, clicks, out detail))
                {
                    method = "message";
                }
                else
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "无法在不移动光标的前提下点击；如接受光标跳变，请显式使用 mode=\"input\"",
                        new JsonObject { ["detail"] = detail });
                }

                break;
            }
        }

        double injectMs = stopwatch.Elapsed.TotalMilliseconds;

        // 大多数点击不会改变前台；只有窗口消息/应用自身激活才需要额外的焦点稳定与归还。
        // keep_foreground=true 时保留目标窗口焦点，供菜单/弹出层连续操作。
        Thread.Sleep(50);
        if (!keepForeground && WindowManager.GetForegroundWindow() != previousForeground)
        {
            Thread.Sleep(50);
            RestoreFocusAfterClick(previousForeground);
        }

        var result = new JsonObject
        {
            ["x"] = x,
            ["y"] = y,
            ["button"] = button,
            ["clicks"] = clicks,
            ["mode"] = mode,
            ["method"] = method,
            ["inject_ms"] = Math.Round(injectMs, 2),
            ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
            ["verified"] = false,
            ["keep_foreground"] = keepForeground,
        };

        if (detail is not null)
        {
            result["detail"] = detail;
        }

        if (method == "message")
        {
            result["hint"] = "消息注入可能被 Electron/Chromium 忽略，请用 wait_for/assert_region 验证";
        }

        return result;
    }

    public JsonObject TypeText(string text, int? pid = null)
    {
        DisplayInfo display = EnsureEnvironment();
        TrackedApp app = RequireTrackedApp(pid);
        RequireWindowOnDisplay(display, app.Hwnd, "type_text");
        IntPtr previous = WindowManager.GetForegroundWindow();
        PauseFocusGuard();

        try
        {
            if (!EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切换到前台，已取消输入以避免打字到用户当前窗口");
            }

            try
            {
                // 前台切换后再取焦点元素：此时才是目标应用内部的焦点控件。
                // 传入 pid 可避免焦点短暂落在宿主/其他进程时把输入验证对象搞错。
                using UiaElementHandle? focused = Uia.GetFocusedElementHandle(app.Pid);
                TextWriteResult write = WriteTextVerified(app, focused, text, replace: false, timeoutMs: 10000);

                return new JsonObject
                {
                    ["typed"] = write.Typed,
                    ["length"] = text.Length,
                    ["target_pid"] = app.Pid,
                    ["chunks"] = write.Chunks,
                    ["text_supported"] = write.TextSupported,
                    ["verified"] = write.Verified,
                    ["method"] = "sendinput",
                    ["inject_ms"] = Math.Round(write.InjectMs, 2),
                    ["duration_ms"] = Math.Round(write.DurationMs, 2),
                    ["observed_length"] = write.ObservedLength,
                    ["warning"] = write.Verified
                        ? null
                        : "输入未能通过 TextPattern/ValuePattern 回读确认；请用 get_element_text 或截图复核",
                };
            }
            finally
            {
                RestoreForeground(previous);
            }
        }
        finally
        {
            ResumeFocusGuard();
        }
    }

    public JsonObject PressKeys(string keys, bool keepForeground = false, int? pid = null) =>
        PressKeySequence(new[] { keys }, keepForeground, pid);

    /// <summary>
    /// 在一次前台租约内发送多个按键序列，供菜单导航（Alt+D → Down → Enter 等）使用。
    /// keepForeground=true 时不归还用户前台，避免菜单在后续调用前被关闭。
    /// </summary>
    public JsonObject PressKeySequence(
        IReadOnlyList<string> keys, bool keepForeground = false, int? pid = null)
    {
        if (keys.Count == 0)
        {
            throw new ToolException("E_INVALID_ARG", "按键序列不能为空");
        }

        DisplayInfo display = EnsureEnvironment();
        TrackedApp app = RequireTrackedApp(pid);
        RequireWindowOnDisplay(display, app.Hwnd, "press_keys");
        IntPtr previous = WindowManager.GetForegroundWindow();
        PauseFocusGuard();

        try
        {
            if (!EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切换到前台，已取消按键以避免输入到用户当前窗口");
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();
                double injectMs = 0;

                foreach (string key in keys)
                {
                    var watch = Stopwatch.StartNew();
                    InputInjector.PressKeys(key);
                    injectMs += watch.Elapsed.TotalMilliseconds;
                    Thread.Sleep(120);
                }

                return new JsonObject
                {
                    ["keys"] = string.Join(",", keys),
                    ["count"] = keys.Count,
                    ["target_pid"] = app.Pid,
                    ["inject_ms"] = Math.Round(injectMs, 2),
                    ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                    ["keep_foreground"] = keepForeground,
                };
            }
            finally
            {
                if (!keepForeground)
                {
                    RestoreForeground(previous);
                }
            }
        }
        finally
        {
            if (!keepForeground)
            {
                ResumeFocusGuard();
            }
        }
    }

    public JsonObject Gesture(JsonElement arguments)
    {
        DisplayInfo display = EnsureEnvironment();

        string type = GetString(arguments, "type") ?? "drag";
        string button = GetString(arguments, "button") ?? "left";
        string mode = (GetString(arguments, "mode") ?? "auto").ToLowerInvariant();
        int duration = GetInt(arguments, "duration_ms", 300);
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

        bool useInput = mode == "input";

        switch (type.ToLowerInvariant())
        {
            case "drag":
            {
                (int fromX, int fromY) = GetPoint(arguments, "from")
                    ?? throw new ToolException("E_INVALID_ARG", "drag 需要 from");
                (int toX, int toY) = GetPoint(arguments, "to")
                    ?? throw new ToolException("E_INVALID_ARG", "drag 需要 to");

                int clampedDuration = Math.Clamp(duration, 20, 5000);
                RequirePointInTrackedApp(display, fromX, fromY, "drag.from");
                RequirePointInTrackedApp(display, toX, toY, "drag.to");
                string? detail = null;

                if (useInput)
                {
                    _cursorGuard?.Pause();
                    PauseFocusGuard();
                    try
                    {
                        InputInjector.Drag(fromX, fromY, toX, toY, button, clampedDuration);
                    }
                    finally
                    {
                        _cursorGuard?.Resume();
                        ResumeFocusGuard();
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

                return new JsonObject
                {
                    ["type"] = "drag",
                    ["from"] = new JsonObject { ["x"] = fromX, ["y"] = fromY },
                    ["to"] = new JsonObject { ["x"] = toX, ["y"] = toY },
                    ["button"] = button,
                    ["method"] = useInput ? "input" : "message",
                    ["detail"] = detail,
                    ["verified"] = false,
                    ["inject_ms"] = Math.Round(injectMs, 2),
                    ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                };
            }

            case "scroll":
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

                RequirePointInTrackedApp(display, x.Value, y.Value, "scroll");

                if (useInput)
                {
                    _cursorGuard?.Pause();
                    PauseFocusGuard();
                    try
                    {
                        InputInjector.Scroll(x, y, dx, dy);
                    }
                    finally
                    {
                        _cursorGuard?.Resume();
                        ResumeFocusGuard();
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

                var scrollResult = new JsonObject
                {
                    ["type"] = "scroll",
                    ["dx"] = dx,
                    ["dy"] = dy,
                    ["method"] = useInput ? "input" : "message",
                    ["detail"] = detail,
                    ["verified"] = false,
                    ["inject_ms"] = Math.Round(injectMs, 2),
                    ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                };

                if (!useInput)
                {
                    scrollResult["hint"] =
                        "窗口消息滚轮可能被未聚焦的应用忽略，请用 assert_region/wait_for 验证";
                }

                return scrollResult;
            }

            case "move":
            {
                (int toX, int toY) = GetPoint(arguments, "to")
                    ?? throw new ToolException("E_INVALID_ARG", "move 需要 to");

                RequirePointInTrackedApp(display, toX, toY, "move.to");
                string? detail = null;

                if (useInput)
                {
                    _cursorGuard?.Pause();
                    PauseFocusGuard();
                    try
                    {
                        InputInjector.Move(toX, toY);
                    }
                    finally
                    {
                        _cursorGuard?.Resume();
                        ResumeFocusGuard();
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

                return new JsonObject
                {
                    ["type"] = "move",
                    ["to"] = new JsonObject { ["x"] = toX, ["y"] = toY },
                    ["method"] = useInput ? "input" : "message",
                    ["detail"] = detail,
                    ["verified"] = false,
                    ["inject_ms"] = Math.Round(injectMs, 2),
                    ["duration_ms"] = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                };
            }

            default:
                throw new ToolException("E_INVALID_ARG", $"未知 gesture 类型: {type}");
        }
    }

    private bool TryUiaInvoke(int x, int y, int pid)
    {
        lock (_gate)
        {
            if (_uiaBlockedPids.Contains(pid))
            {
                return false;
            }
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            bool ok = Uia.TryInvokeAtPoint(x, y, pid, out _);

            // 没有 InvokePattern 会快速返回 false；超过阈值说明 provider 明显卡顿，
            // 本会话内对该 pid 熔断，后续 auto 直接走 message，避免反复挂起。
            if (!ok && stopwatch.ElapsedMilliseconds >= 800)
            {
                lock (_gate)
                {
                    _uiaBlockedPids.Add(pid);
                }
            }

            return ok;
        }
        catch
        {
            lock (_gate)
            {
                _uiaBlockedPids.Add(pid);
            }

            return false;
        }
    }

    private sealed record TextWriteResult(
        int Typed,
        int Chunks,
        bool TextSupported,
        bool Verified,
        int? ObservedLength,
        double InjectMs,
        double DurationMs);

    /// <summary>
    /// 把文本分块写入当前前台目标，并在每块后回读确认。
    /// 关键点：只有确认完成后调用方才允许归还前台，避免剩余按键落到用户窗口。
    /// </summary>
    private TextWriteResult WriteTextVerified(
        TrackedApp app,
        UiaElementHandle? target,
        string text,
        bool replace,
        int timeoutMs)
    {
        string? before = target is null ? null : Uia.GetText(target.Element, 65536);
        bool textSupported = before is not null;
        var stopwatch = Stopwatch.StartNew();
        double injectMs = 0;
        int typed = 0;
        int chunks = 0;
        string sent = string.Empty;

        foreach (string chunk in SplitTextChunks(text, 16))
        {
            if (WindowManager.GetForegroundWindow() != app.Hwnd)
            {
                throw new ToolException(
                    "E_FOCUS_LOST",
                    "输入过程中目标窗口失去前台，已中止以避免输入到用户窗口",
                    new JsonObject
                    {
                        ["target_pid"] = app.Pid,
                        ["typed"] = typed,
                        ["chunks"] = chunks,
                    });
            }

            var chunkWatch = Stopwatch.StartNew();
            typed += InputInjector.TypeText(chunk);
            injectMs += chunkWatch.Elapsed.TotalMilliseconds;
            sent += chunk;
            chunks++;

            if (textSupported && target is not null)
            {
                int remaining = Math.Max(0, timeoutMs - (int)stopwatch.ElapsedMilliseconds);
                int chunkTimeout = Math.Clamp(500 + sent.Length * 8, 500, Math.Max(500, remaining));

                if (!WaitForText(target.Element, before!, sent, replace, chunkTimeout))
                {
                    string? observed = Uia.GetText(target.Element, 65536);

                    return new TextWriteResult(
                        typed,
                        chunks,
                        TextSupported: true,
                        Verified: false,
                        ObservedLength: observed?.Length,
                        InjectMs: injectMs,
                        DurationMs: stopwatch.Elapsed.TotalMilliseconds);
                }
            }
            else
            {
                // 没有 TextPattern/ValuePattern 时退回保守节流；无法精确确认。
                Thread.Sleep(Math.Clamp(25 + chunk.Length * 2, 35, 80));
            }
        }

        // 不支持回读时至少给应用留出消费时间，并确认前台仍然属于目标。
        if (!textSupported)
        {
            int settle = Math.Clamp(120 + text.Length * 4, 180, 1500);
            var deadline = DateTime.UtcNow.AddMilliseconds(settle);

            while (DateTime.UtcNow < deadline)
            {
                if (WindowManager.GetForegroundWindow() != app.Hwnd)
                {
                    throw new ToolException(
                        "E_FOCUS_LOST",
                        "输入结束前目标窗口失去前台，请用 get_element_text/截图复核",
                        new JsonObject { ["target_pid"] = app.Pid, ["typed"] = typed });
                }

                Thread.Sleep(20);
            }
        }

        string? after = target is null ? null : Uia.GetText(target.Element, 65536);
        bool verified = textSupported &&
                        after is not null &&
                        TextMatches(before!, after, sent, replace);

        return new TextWriteResult(
            typed,
            chunks,
            textSupported,
            verified,
            after?.Length,
            injectMs,
            stopwatch.Elapsed.TotalMilliseconds);
    }

    private bool WaitForText(
        IUIAutomationElement element,
        string before,
        string sent,
        bool replace,
        int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            string? after = Uia.GetText(element, 65536);

            if (after is not null && TextMatches(before, after, sent, replace))
            {
                return true;
            }

            Thread.Sleep(25);
        }

        return false;
    }

    private static bool TextMatches(string before, string after, string sent, bool replace)
    {
        string expected = NormalizeForCompare(sent);
        string actual = NormalizeForCompare(after);
        string baseline = NormalizeForCompare(before);

        if (expected.Length == 0)
        {
            // replace 空值必须确认目标确实已清空；insert/append 空值是 no-op，
            // 要求正文保持不变（避免把“什么都没写”误判为写入成功）。
            return replace ? actual.Length == 0 : actual == baseline;
        }

        if (replace)
        {
            // 替换模式下，正文应以新值结尾（Word 可能补一个段落标记）。
            return actual.TrimEnd('\n').EndsWith(expected.TrimEnd('\n'), StringComparison.Ordinal);
        }

        // 插入/追加模式下，既要求内容出现，也要求长度增长，避免“原本就有同样文字”误判。
        return actual.Contains(expected, StringComparison.Ordinal) &&
               actual.Length >= baseline.Length + expected.Length - 2;
    }

    private static string NormalizeForCompare(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');

    private static IEnumerable<string> SplitTextChunks(string text, int chunkChars)
    {
        int index = 0;

        while (index < text.Length)
        {
            int take = Math.Min(chunkChars, text.Length - index);

            // 不要把代理对拆到两个 SendInput 批次。
            if (take > 1 &&
                char.IsHighSurrogate(text[index + take - 1]) &&
                index + take < text.Length)
            {
                take--;
            }

            yield return text.Substring(index, take);
            index += take;
        }
    }

    private bool EnsureForeground(TrackedApp app)
    {
        if (WindowManager.GetForegroundWindow() == app.Hwnd)
        {
            return true;
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            WindowManager.ForceForeground(app.Hwnd);
            if (WindowManager.GetForegroundWindow() == app.Hwnd)
            {
                return true;
            }

            Thread.Sleep(40);
        }

        return WindowManager.GetForegroundWindow() == app.Hwnd;
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

        TrackedApp? app = LastTrackedApp();
        WindowInfo? info = WindowManager.FromHandle(current);

        // 点击可能打开了应用内的菜单/弹窗；保留其焦点，避免把刚打开的 UI 关掉。
        if (app is not null && info is not null && info.Pid == app.Pid && current != app.Hwnd)
        {
            return;
        }

        RestoreForeground(previous);
    }

    private void RestoreForeground(IntPtr previous, bool logFailure = true)
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
                    _log.Warn($"恢复前台窗口失败 hwnd=0x{previous.ToInt64():X}");
                }
            }
            else if (logFailure && NativeMethods.GetForegroundWindow() != previous)
            {
                _log.Warn(
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

    private TrackedApp RequireTrackedApp(int? pid = null)
    {
        TrackedApp? app = FindLiveTrackedApp(pid);

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

    private TrackedApp RequirePointInTrackedApp(DisplayInfo display, int x, int y, string label)
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

        NativeMethods.GetWindowThreadProcessId(target, out uint targetPid);
        NativeMethods.GetWindowThreadProcessId(root, out uint rootPid);

        TrackedApp? tracked;
        lock (_gate)
        {
            // 命中窗口或其顶层根窗口属于本会话应用即可（兼容 UWP/宿主窗口）。
            // 同时校验窗口仍属于登记时的进程，避免 PID 复用后误判为可操作目标。
            tracked = _apps.FirstOrDefault(
                app => (app.Pid == (int)targetPid || app.Pid == (int)rootPid) &&
                       IsLiveTrackedApp(app));
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

    private static void RequireWindowOnDisplay(DisplayInfo display, IntPtr hwnd, string label)
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

    private static void RequirePointInDisplay(DisplayInfo display, int x, int y, string label)
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

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : fallback;

    private static double GetDouble(JsonElement element, string property, double fallback) =>
        element.TryGetProperty(property, out JsonElement value) && value.TryGetDouble(out double result)
            ? result
            : fallback;

    private static bool GetBool(JsonElement element, string property, bool fallback) =>
        element.TryGetProperty(property, out JsonElement value) &&
        (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            ? value.GetBoolean()
            : fallback;
}
