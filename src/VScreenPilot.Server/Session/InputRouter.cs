using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Views;
using Interop.UIAutomationClient;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Input;
using VScreenPilot.Json;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using static VScreenPilot.Mcp.JsonArgReader;
using ObserveSignature = VScreenPilot.Verification.ObserveSampler.Signature;

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
internal sealed class InputRouter
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

    /// <summary>按 element_ref 点击：优先 UIA pattern，其次消息/输入通道。</summary>
    public JsonObject ClickElementRef(
        string reference, string? channel, bool keepForeground, bool fallbackInput = false)
    {
        DisplayInfo display = _host.EnsureDisplay();
        ElementRefEntry probe = _host.RequireElement(reference);
        string mode = (channel ?? "auto").Trim().ToLowerInvariant();

        if (mode is not ("auto" or "uia" or "message" or "input"))
        {
            throw new ToolException("E_INVALID_ARG", $"未知 click_ref channel: {mode}");
        }

        // MSAA 控件没有 UIA 句柄：显式 channel 仍要遵守，不能一律走默认动作。
        if (MsaaService.TryParse(probe.RuntimeId, out IntPtr msaaHwnd, out int msaaOrdinal))
        {
            bool acted = mode is "auto" or "uia" &&
                         _host.Uia.InvokeMsaa(() => MsaaService.DoDefaultAction(msaaHwnd, msaaOrdinal));
            var bounds = _host.Uia.InvokeMsaa(() => MsaaService.GetBounds(msaaHwnd, msaaOrdinal));
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

            return new JsonObject
            {
                ["clicked"] = true,
                ["ref"] = probe.Ref,
                ["channel"] = resultChannel,
                ["refreshed"] = false,
                ["observed"] = new JsonObject { ["present"] = true },
                ["bounds"] = bounds is null
                    ? new JsonObject()
                    : new JsonObject
                    {
                        ["x"] = bounds.Value.Left,
                        ["y"] = bounds.Value.Top,
                        ["width"] = bounds.Value.Width,
                        ["height"] = bounds.Value.Height,
                    },
            };
        }

        using UiaElementHandle handle = _host.ResolveHandle(
            reference, display, out ElementRefEntry entry, out bool refreshed);

        UiaElementInfo info = handle.Info;

        if (mode is "auto" or "uia")
        {
            (bool invoked, string? strategy) = handle.Invoke(UiaInvokePattern.Auto);

            if (invoked)
            {
                return new JsonObject
                {
                    ["clicked"] = true,
                    ["ref"] = entry.Ref,
                    ["channel"] = "uia",
                    ["pattern"] = strategy,
                    ["refreshed"] = refreshed,
                    ["observed"] = ObserveAfterAction(entry),
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = info.Left, ["y"] = info.Top,
                        ["width"] = info.Width, ["height"] = info.Height,
                    },
                };
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

        JsonObject result = VspJson.ToNode(clickResult).AsObject();
        result["clicked"] = true;
        result["ref"] = entry.Ref;
        result["refreshed"] = refreshed;
        result["observed"] = ObserveAfterAction(entry);
        return result;
    }

    /// <summary>
    /// 动作后的轻量观察：UIA 返回成功不代表应用真的响应了。
    /// 这里只报告控件是否还在/是否可用/是否获得焦点，失败不影响动作结果。
    /// </summary>
    private JsonObject ObserveAfterAction(ElementRefEntry entry)
    {
        try
        {
            using UiaElementHandle? handle = string.IsNullOrEmpty(entry.RuntimeId)
                ? null
                : _host.Uia.FindByRuntimeId(entry.Hwnd, entry.RuntimeId);

            if (handle is null)
            {
                return new JsonObject { ["present"] = false };
            }

            return new JsonObject
            {
                ["present"] = true,
                ["enabled"] = handle.Info.Enabled,
                ["focused"] = handle.Info.Focused,
                ["offscreen"] = handle.Info.Offscreen,
            };
        }
        catch (Exception exception)
        {
            return new JsonObject
            {
                ["present"] = null,
                ["error"] = exception.Message,
            };
        }
    }

    public ClickView Click(
        int x, int y, string button, int clicks, string mode,
        bool keepForeground = false, bool observe = true, bool fallbackInput = false)
    {
        DisplayInfo display = _host.EnsureDisplay();

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

        if (mode == "input" && !_host.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }

        TrackedApp app = _host.RequirePointInTrackedApp(display, x, y, "click");

        var observeWatch = Stopwatch.StartNew();
        ObserveSignature? observeBefore = observe
            ? _host.CaptureSignature(display, x - 60, y - 60, 120, 120)
            : null;

        string method;
        string? detail = null;
        var stopwatch = Stopwatch.StartNew();
        IntPtr previousForeground = WindowManager.GetForegroundWindow();

        if (keepForeground)
        {
            // 菜单/弹出层需要连续操作时，暂时冻结焦点护栏；租约到期后自动恢复，
            // 避免异常路径或 agent 不再调用工具时永久关闭护栏。
            _host.PauseFocusGuard(30000);
        }

        bool canUseUia = button.Equals("left", StringComparison.OrdinalIgnoreCase) && clicks == 1;

        switch (mode)
        {
            case "input":
            {
                _host.PauseCursor();
                _host.PauseFocusGuard();
                try
                {
                    InputInjector.Click(x, y, button, clicks);
                }
                finally
                {
                    _host.ResumeCursor();

                    if (!keepForeground)
                    {
                        _host.ResumeFocusGuard();
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
                RejectNonClientArea(app, x, y, "message");

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
                else if (!IsNonClientPoint(app.Hwnd, x, y) &&
                         MessageInput.Click(x, y, button, clicks, out detail))
                {
                    method = "message";
                }
                else if (allowInputFallback)
                {
                    // fallback 也要检查交互桌面：锁屏/安全桌面下 SendInput 只会得到
                    // 模糊的 E_INPUT，提前给出 E_NO_INTERACTIVE_DESKTOP 更有用。
                    if (!_host.IsInputDesktopAvailable())
                    {
                        throw new ToolException(
                            "E_NO_INTERACTIVE_DESKTOP",
                            "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                            retryable: true,
                            hint: "解锁桌面或改用 UIA 语义操作",
                            stage: "input");
                    }

                    _host.PauseCursor();
                    _host.PauseFocusGuard();

                    try
                    {
                        InputInjector.Click(x, y, button, clicks);
                    }
                    finally
                    {
                        _host.ResumeCursor();

                        if (!keepForeground)
                        {
                            _host.ResumeFocusGuard();
                        }
                    }

                    method = "input-fallback";
                }
                else
                {
                    throw new ToolException(
                        "E_INPUT_UNSUPPORTED",
                        "无法在不移动光标的前提下点击；如接受光标跳变，请显式使用 mode=\"input\" 或 fallback_input=true",
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

        ObserveSignature? observeAfter = observeBefore is null
            ? null
            : _host.CaptureSignature(display, x - 60, y - 60, 120, 120);
        ObservedView? observed = _host.BuildObserved(
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

    public TypeTextView TypeText(
        string text, int? pid = null, bool verify = true, int chunkChars = 32, string? channel = null)
    {
        if (text.Length > 100_000)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"type_text 文本过长（{text.Length} 字符，上限 100000）；请分段输入",
                retryable: false,
                stage: "input");
        }

        if (!_host.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }

        DisplayInfo display = _host.Ensure();
        TrackedApp app = RequireTrackedApp(pid);
        _host.RequireWindowOnDisplay(display, app.Hwnd, "type_text");
        using var inputLease = InputLease.TryAcquire(3000, "type_text");
        IntPtr previous = WindowManager.GetForegroundWindow();
        _host.PauseFocusGuard();
        int effectiveChunk = Math.Clamp(chunkChars, 1, 256);
        int verifyEvery = text.Length > 20000 ? 16 : text.Length > 2048 ? 4 : 1;

        try
        {
            if (!_host.EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切换到前台，已取消输入以避免打字到用户当前窗口",
                    retryable: true,
                    hint: "确认没有模态框禁用主窗口；可改用 click 的消息点击或先关闭弹窗",
                    stage: "focus");
            }

            try
            {
                // 前台切换后再取焦点元素：此时才是目标应用内部的焦点控件。
                // 传入 pid 可避免焦点短暂落在宿主/其他进程时把输入验证对象搞错。
                using UiaElementHandle? focused = _host.Uia.GetFocusedElementHandle(app.Pid);

                // 同进程可能有多个文档窗口/对话框：焦点元素必须落在当前前台窗口内，
                // 否则宁可中止，也不要把文本打进另一个文档。
                if (focused is not null &&
                    !ElementBelongsToWindow(focused.Info, WindowManager.GetForegroundWindow()))
                {
                    throw new ToolException(
                        "E_FOCUS",
                        "焦点元素不属于当前前台窗口，已取消输入以避免写错文档",
                        new JsonObject
                        {
                            ["target_pid"] = app.Pid,
                            ["element"] = ElementPresenter.ToJson(focused.Info),
                        },
                        retryable: true,
                        hint: "先 click 目标控件或把目标窗口切到前台",
                        stage: "focus");
                }

                // 长文本逐字符 SendInput 约 3.8ms/字；剪贴板一次性粘贴通常 <200ms。
                // 代理对（emoji）即使很短也走剪贴板：逐码元 SendInput 在部分应用会丢字符。
                string mode = (channel ?? "auto").Trim().ToLowerInvariant();
                bool hasSurrogate = false;

                foreach (char ch in text)
                {
                    if (char.IsSurrogate(ch))
                    {
                        hasSurrogate = true;
                        break;
                    }
                }

                bool patternUsed = false;
                bool useClipboard = false;
                TextWriteResult write;

                if (mode == "pattern")
                {
                    if (focused is null)
                    {
                        throw new ToolException(
                            "E_PATTERN_UNSUPPORTED",
                            "当前焦点控件不可用，无法用 ValuePattern 写入",
                            retryable: false,
                            hint: "改用 channel=auto 或 channel=clipboard",
                            stage: "input");
                    }

                    UiaSetValueResult set = focused.SetValue(text);

                    if (!set.Supported || set.ReadOnly || set.Failure is not null)
                    {
                        throw new ToolException(
                            "E_PATTERN_UNSUPPORTED",
                            "控件不支持可写 ValuePattern",
                            new JsonObject { ["failure"] = set.Failure },
                            retryable: false,
                            hint: "改用 channel=auto 或 channel=clipboard",
                            stage: "input");
                    }

                    patternUsed = true;
                    write = new TextWriteResult(
                        text.Length, Chunks: 1, TextSupported: true,
                        Verified: string.Equals(set.Value, text, StringComparison.Ordinal),
                        ObservedLength: set.Value?.Length,
                        InjectMs: 0, DurationMs: 0);
                }
                else
                {
                    useClipboard = mode == "clipboard" ||
                                   (mode == "auto" && (text.Length >= 200 || hasSurrogate));

                    if (useClipboard && ClipboardText.HasNonTextFormats())
                    {
                        // 剪贴板里有图片/文件：写文本会永久清掉它们，且无法还原。
                        if (mode == "clipboard")
                        {
                            throw new ToolException(
                                "E_CLIPBOARD_UNSAFE",
                                "剪贴板包含非文本内容（图片/文件），写入文本会清空且无法还原",
                                retryable: false,
                                hint: "改用 channel=auto/keys（SendInput），或先让用户备份剪贴板",
                                stage: "input");
                        }

                        useClipboard = false;
                    }

                    if (useClipboard)
                    {
                        write = WriteTextViaClipboard(focused, text);
                    }
                    else
                    {
                        write = verify
                            ? WriteTextVerified(
                                app, focused, text, replace: false, timeoutMs: 10000,
                                chunkChars: effectiveChunk, verifyEveryChunks: verifyEvery)
                            : WriteTextUnverified(app, focused, text);
                    }
                }

                // 写入前焦点控件偶发不可用（UWP/新版记事本）；写完后再取一次补校验，
                // 避免“内容其实写对了，但 verified 永远是 false”。
                if (!write.Verified && verify && focused is null && !patternUsed && !useClipboard)
                {
                    using UiaElementHandle? late = _host.Uia.GetFocusedElementHandle(app.Pid);
                    string? observed = late?.GetText(65536);

                    if (observed is not null)
                    {
                        write = write with
                        {
                            TextSupported = true,
                            Verified = TextMatches(string.Empty, observed, text, replace: false),
                            ObservedLength = observed.Length,
                        };
                    }
                }

                return new TypeTextView(
                    write.Typed,
                    text.Length,
                    app.Pid,
                    write.Chunks,
                    effectiveChunk,
                    write.TextSupported,
                    write.Verified,
                    patternUsed
                        ? "uia-value-pattern"
                        : useClipboard
                            ? "clipboard+ctrl-v"
                            : verify ? "sendinput+verify" : "sendinput",
                    patternUsed ? "pattern" : useClipboard ? "clipboard" : "keys",
                    Math.Round(write.InjectMs, 2),
                    Math.Round(write.DurationMs, 2),
                    write.ObservedLength,
                    write.Verified
                        ? null
                        : "输入未能通过 TextPattern/ValuePattern 回读确认；请用 get_element_text 或截图复核");
            }
            finally
            {
                _host.RestoreForeground(previous);
            }
        }
        finally
        {
            _host.ResumeFocusGuard();
        }
    }

    public PressKeysView PressKeys(
        string keys, bool keepForeground = false, int? pid = null,
        bool allowSystemKeys = false, bool observe = true) =>
        PressKeySequence(new[] { keys }, keepForeground, pid, allowSystemKeys, observe);

    /// <summary>
    /// 在一次前台租约内发送多个按键序列，供菜单导航（Alt+D → Down → Enter 等）使用。
    /// keepForeground=true 时不归还用户前台，避免菜单在后续调用前被关闭。
    /// </summary>
    public PressKeysView PressKeySequence(
        IReadOnlyList<string> keys,
        bool keepForeground = false,
        int? pid = null,
        bool allowSystemKeys = false,
        bool observe = true)
    {
        if (keys.Count == 0)
        {
            throw new ToolException("E_INVALID_ARG", "按键序列不能为空");
        }

        foreach (string key in keys)
        {
            SystemKeyGuard.ThrowIfBlocked(key, allowSystemKeys);
        }

        using var inputLease = InputLease.TryAcquire(3000, "press_keys");

        if (!_host.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }

        DisplayInfo display = _host.Ensure();
        TrackedApp app = RequireTrackedApp(pid);
        _host.RequireWindowOnDisplay(display, app.Hwnd, "press_keys");
        IntPtr previous = WindowManager.GetForegroundWindow();
        _host.PauseFocusGuard(keepForeground ? 30000 : 0);

        try
        {
            if (!_host.EnsureForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS",
                    "无法把目标窗口切换到前台，已取消按键以避免输入到用户当前窗口",
                    retryable: true,
                    hint: "确认没有模态框禁用主窗口；可改用消息点击或先关闭弹窗",
                    stage: "focus");
            }

            try
            {
                var stopwatch = Stopwatch.StartNew();
                double injectMs = 0;

                ObserveSignature? observeBefore = observe
                    ? _host.CaptureSignature(display, display.X, display.Y, display.Width, display.Height)
                    : null;

                int sent = 0;

                foreach (string key in keys)
                {
                    if (_host.Cancellation.IsCancellationRequested)
                    {
                        throw new ToolException(
                            "E_CANCELLED",
                            "press_keys 已被取消，停止继续注入",
                            new JsonObject { ["sent"] = sent },
                            retryable: true,
                            hint: "重新调用；已发送的按键不会自动撤销",
                            stage: "input");
                    }

                    var watch = Stopwatch.StartNew();
                    InputInjector.PressKeys(key, allowSystemKeys);
                    sent++;
                    injectMs += watch.Elapsed.TotalMilliseconds;
                    Thread.Sleep(120);
                }

                ObserveSignature? observeAfter = observeBefore is null
                    ? null
                    : _host.CaptureSignature(display, display.X, display.Y, display.Width, display.Height);
                ObservedView? observed = _host.BuildObserved(
                    observeBefore, observeAfter, stopwatch.Elapsed.TotalMilliseconds);

                return new PressKeysView(
                    string.Join(",", keys),
                    keys.Count,
                    app.Pid,
                    Math.Round(injectMs, 2),
                    Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
                    keepForeground,
                    observed,
                    observed?.Changed);
            }
            finally
            {
                if (!keepForeground)
                {
                    _host.RestoreForeground(previous);
                }
            }
        }
        finally
        {
            if (!keepForeground)
            {
                _host.ResumeFocusGuard();
            }
        }
    }

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

        switch (type.ToLowerInvariant())
        {
            case "drag":
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

            case "move":
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

            default:
                throw new ToolException("E_INVALID_ARG", $"未知 gesture 类型: {type}");
        }
    }

    private void BlockUiaPid(int pid)
    {
        lock (_blockGate)
        {
            string clientId = _host.Clients.CurrentId;

            if (!_uiaBlockedPids.TryGetValue(clientId, out HashSet<int>? blocked))
            {
                blocked = new HashSet<int>();
                _uiaBlockedPids[clientId] = blocked;
            }

            blocked.Add(pid);
        }
    }

    private bool TryUiaInvoke(int x, int y, int pid)
    {
        lock (_blockGate)
        {
            if (_uiaBlockedPids.TryGetValue(_host.Clients.CurrentId, out HashSet<int>? blocked) &&
                blocked.Contains(pid))
            {
                return false;
            }
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // ElementFromPoint/Invoke 可能被 provider 卡住；最多等 3s，超时换新 UIA 实例。
            Task<bool> invokeTask = Task.Run(() =>
            {
                bool ok = _host.Uia.TryInvokeAtPoint(x, y, pid, out _);
                return ok;
            });

            if (!invokeTask.Wait(3000))
            {
                _host.ResetUia();
                BlockUiaPid(pid);
                _log.Warn($"UIA 坐标触发超时，已熔断 pid={pid}");
                return false;
            }

            bool result = invokeTask.Result;

            // 没有 InvokePattern 会快速返回 false；超过阈值说明 provider 明显卡顿，
            // 本会话内对该 pid 熔断，后续 auto 直接走 message，避免反复挂起。
            if (!result && stopwatch.ElapsedMilliseconds >= 800)
            {
                BlockUiaPid(pid);
            }

            return result;
        }
        catch
        {
            BlockUiaPid(pid);
            return false;
        }
    }

    /// <summary>
    /// 把文本分块写入当前前台目标，并在每块后回读确认。
    /// 关键点：只有确认完成后调用方才允许归还前台，避免剩余按键落到用户窗口。
    /// </summary>
    public TextWriteResult WriteTextVerified(
        TrackedApp app,
        UiaElementHandle? target,
        string text,
        bool replace,
        int timeoutMs,
        int chunkChars = 32,
        int verifyEveryChunks = 1)
    {
        string? before = target is null ? null : _host.Uia.GetText(target.Element, 65536);
        bool textSupported = before is not null;
        var stopwatch = Stopwatch.StartNew();
        double injectMs = 0;
        int typed = 0;
        int chunks = 0;
        var sentBuilder = new StringBuilder(text.Length);

        foreach (string chunk in SplitTextChunks(text, chunkChars))
        {
            if (_host.Cancellation.IsCancellationRequested)
            {
                throw new ToolException(
                    "E_CANCELLED",
                    "type_text 已被取消，停止继续注入",
                    new JsonObject { ["typed"] = typed, ["chunks"] = chunks },
                    retryable: true,
                    hint: "重新调用 type_text；已输入的内容不会自动撤销",
                    stage: "input");
            }

            if (!_host.IsAppForeground(app))
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
            sentBuilder.Append(chunk);
            chunks++;
            ToolProgress.Report(
                Math.Min(sentBuilder.Length, text.Length),
                Math.Max(1, text.Length),
                $"type_text 已输入 {sentBuilder.Length}/{text.Length}");

            // 长文本逐块读全文是 O(n²)；每 N 块确认一次，最后再整体确认。
            if (textSupported && target is not null)
            {
                if (chunks % Math.Max(1, verifyEveryChunks) == 0)
                {
                    int remaining = Math.Max(0, timeoutMs - (int)stopwatch.ElapsedMilliseconds);
                    int chunkTimeout = Math.Clamp(
                        500 + sentBuilder.Length * 8, 500, Math.Max(500, remaining));

                    if (!WaitForText(target, before!, sentBuilder.ToString(), replace, chunkTimeout))
                    {
                        string? observed = target.GetText(65536);

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
                if (!_host.IsAppForeground(app))
                {
                    throw new ToolException(
                        "E_FOCUS_LOST",
                        "输入结束前目标窗口失去前台，请用 get_element_text/截图复核",
                        new JsonObject { ["target_pid"] = app.Pid, ["typed"] = typed });
                }

                Thread.Sleep(20);
            }
        }

        string? after = target is null ? null : target.GetText(65536);
        bool verified = textSupported &&
                        after is not null &&
                        TextMatches(before!, after, sentBuilder.ToString(), replace);

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
        UiaElementHandle target,
        string before,
        string sent,
        bool replace,
        int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(200, timeoutMs));

        while (DateTime.UtcNow < deadline)
        {
            string? after = target.GetText(65536);

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

    /// <summary>快速路径：一次 SendInput 写完，不做逐块回读；调用方需自行复核。</summary>
    /// <summary>
    /// 剪贴板粘贴：一次性写入，避免 O(n) 的 SendInput 与 IME 组合问题。
    /// 会保存/还原原有文本剪贴板；非文本内容无法还原，因此只在长文本或显式要求时使用。
    /// </summary>
    private TextWriteResult WriteTextViaClipboard(UiaElementHandle? target, string text)
    {
        var stopwatch = Stopwatch.StartNew();
        string? before = target?.GetText(65536);
        string? previous = null;

        try
        {
            previous = ClipboardText.Capture();
        }
        catch (Exception exception)
        {
            // 读不到就当作空剪贴板。
            _ = exception;
        }

        try
        {
            ClipboardText.SetText(text);
            InputInjector.PressKeys("Ctrl+V");
            Thread.Sleep(Math.Clamp(60 + text.Length / 20, 80, 1200));
        }
        finally
        {
            try
            {
                ClipboardText.Restore(previous);
            }
            catch (Exception exception)
            {
                // 还原失败不影响输入结果。
                _ = exception;
            }
        }

        string? after = target?.GetText(65536);
        bool verified = before is not null &&
                        after is not null &&
                        after.Length >= before.Length + text.Length;

        return new TextWriteResult(
            text.Length,
            Chunks: 1,
            TextSupported: before is not null,
            Verified: verified,
            ObservedLength: after?.Length,
            InjectMs: stopwatch.Elapsed.TotalMilliseconds,
            DurationMs: stopwatch.Elapsed.TotalMilliseconds);
    }

    private TextWriteResult WriteTextUnverified(
        TrackedApp app,
        UiaElementHandle? target,
        string text)
    {
        string? before = target is null ? null : _host.Uia.GetText(target.Element, 65536);
        var stopwatch = Stopwatch.StartNew();

        if (!_host.IsAppForeground(app))
        {
            throw new ToolException(
                "E_FOCUS_LOST",
                "输入前目标窗口失去前台，已中止",
                retryable: true,
                stage: "focus");
        }

        var injectWatch = Stopwatch.StartNew();
        int typed = InputInjector.TypeText(text);
        double injectMs = injectWatch.Elapsed.TotalMilliseconds;
        Thread.Sleep(Math.Clamp(120 + text.Length * 2, 200, 1500));

        string? after = target is null ? null : target.GetText(65536);
        return new TextWriteResult(
            typed,
            Chunks: text.Length == 0 ? 0 : 1,
            TextSupported: before is not null,
            Verified: false,
            ObservedLength: after?.Length,
            InjectMs: injectMs,
            DurationMs: stopwatch.Elapsed.TotalMilliseconds);
    }

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
