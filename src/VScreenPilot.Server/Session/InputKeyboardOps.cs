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

/// <summary>写入通道的选择。</summary>
internal sealed record WriteChannelPlan(string? Channel, bool Verify, int ChunkChars, int VerifyEvery);

/// <summary>KeyboardOps：InputRouter 的输入子通道。</summary>
/// <summary>写入后补校验的上下文。</summary>
internal sealed record ReprobeContext(
    TrackedApp App, UiaElementHandle? Focused, string Text, bool Verify,
    bool PatternUsed, bool UseClipboard);

/// <summary>type_text 的写入结果。</summary>
internal sealed record TypeTextOutcome(TextWriteResult Write, bool PatternUsed, bool UseClipboard);

/// <summary>type_text 的选项。</summary>
internal sealed record TypeTextSettings(int TextLength, int ChunkChars, bool Verify);

internal sealed class InputKeyboardOps
{
    internal readonly InputRouter _host;

    public InputKeyboardOps(InputRouter host) => _host = host;
    public TypeTextView TypeText(
        string text, int? pid = null, bool verify = true, int chunkChars = 32, string? channel = null,
        long? hwnd = null, bool strictVerify = false, int paceMs = 0, string? target = null)
    {
        ValidateTextRequest(text);

        DisplayInfo display = _host._display.Ensure();
        (TrackedApp app, IntPtr targetHwnd) = _host.RequireInputTarget(pid, hwnd, target);
        BlockingDialogGuard.RequireNotBlocked(targetHwnd, "type_text");
        _host._display.RequireWindowOnDisplay(display, targetHwnd, "type_text");
        using var inputLease = InputLease.TryAcquire(3000, "type_text");
        IntPtr previous = WindowManager.GetForegroundWindow();
        _host._focus.PauseFocusGuard(0);
        int effectiveChunk = Math.Clamp(chunkChars, 1, 256);
        int verifyEvery = ResolveVerifyEvery(text.Length, effectiveChunk);

        try
        {
            bool exact = hwnd is not null || IsExplicitTarget(target);
            EnsureForegroundForTyping(app, targetHwnd, exact: exact);

            try
            {
                using UiaElementHandle? focused = FocusedElementInForeground(app);

                (TextWriteResult write, bool patternUsed, bool useClipboard) = WriteByChannel(
                    app, focused,
                    new TextWriteRequest(
                        text, Replace: false, TimeoutMs: 12000,
                        ChunkChars: effectiveChunk, VerifyEveryChunks: verifyEvery,
                        PaceMs: Math.Clamp(paceMs, 0, 2000)),
                    new WriteChannelPlan(channel, verify, effectiveChunk, verifyEvery));

                write = ReprobeWriteResult(write, new ReprobeContext(app, focused, text, verify, patternUsed, useClipboard));

                TypeTextView view = BuildTypeTextView(
                    app,
                    new TypeTextOutcome(write, patternUsed, useClipboard),
                    new TypeTextSettings(text.Length, effectiveChunk, verify));

                if (strictVerify && !view.Verified)
                {
                    throw new ToolException(
                        "E_VERIFY_FAILED",
                        "type_text 写入后回读与期望不一致",
                        new JsonObject
                        {
                            ["expected_length"] = text.Length,
                            ["observed_length"] = view.ObservedLength,
                            ["channel"] = view.Channel,
                            ["target_pid"] = app.Pid,
                            ["hwnd"] = targetHwnd.ToInt64(),
                            ["warning"] = view.Warning,
                        },
                        retryable: false,
                        hint: "确认焦点控件正确、目标未被弹窗拦截；必要时用 get_element_text/截图复核",
                        stage: "verify");
                }

                return view;
            }
            finally
            {
                if (!_host.Lease.IsActive(_host._session.Clients.CurrentId))
                {
                    _host._focus.RestoreForeground(previous);
                }
            }
        }
        finally
        {
            if (!_host.Lease.IsActive(_host._session.Clients.CurrentId))
            {
                _host._focus.ResumeFocusGuard();
            }
        }
    }

    private static int ResolveVerifyEvery(int length, int chunkChars)
    {
        int baseline = length > 20000 ? 16 : length > 2048 ? 4 : 1;

        // 自绘控件常用 chunk_chars=1；此时逐块全文回读是 O(n²)，
        // 自动把校验间隔拉到 10 块一次，兼顾可靠性与速度。
        if (chunkChars <= 4)
        {
            return Math.Max(baseline, 10);
        }

        if (chunkChars <= 16)
        {
            return Math.Max(baseline, 4);
        }

        return baseline;
    }

    private TextWriteResult ReprobeWriteResult(TextWriteResult write, ReprobeContext context)
    {
        TrackedApp app = context.App;
        UiaElementHandle? focused = context.Focused;
        string text = context.Text;
        bool verify = context.Verify;
        bool patternUsed = context.PatternUsed;
        bool useClipboard = context.UseClipboard;
        // 写入前焦点控件偶发不可用（UWP/新版记事本）；写完后再次取一次补校验。
        // 避免“内容其实写对了，但 verified 永远是 false”。
        if (write.Verified || !verify || focused is not null || patternUsed || useClipboard)
        {
            return write;
        }

        using UiaElementHandle? late = _host._uia.Uia.GetFocusedElementHandle(app.Pid);
        string? observed = late?.GetText(65536);

        if (observed is null)
        {
            return write;
        }

        return write with
        {
            TextSupported = true,
            Verified = _host.TextOps.TextMatches(string.Empty, observed, text, replace: false),
            ObservedLength = observed.Length,
        };
    }

    private static TypeTextView BuildTypeTextView(
        TrackedApp app, TypeTextOutcome outcome, TypeTextSettings settings) =>
        BuildTypeTextViewCore(app, outcome.Write, settings, outcome.PatternUsed, outcome.UseClipboard);

    private static TypeTextView BuildTypeTextViewCore(
        TrackedApp app,
        TextWriteResult write,
        TypeTextSettings settings,
        bool patternUsed,
        bool useClipboard) =>
        new(
            write.Typed,
            settings.TextLength,
            app.Pid,
            write.Chunks,
            settings.ChunkChars,
            write.TextSupported,
            write.Verified,
            patternUsed
                ? "uia-value-pattern"
                : useClipboard
                    ? "clipboard+ctrl-v"
                    : settings.Verify ? "sendinput+verify" : "sendinput",
            patternUsed ? "pattern" : useClipboard ? "clipboard" : "keys",
            Math.Round(write.InjectMs, 2),
            Math.Round(write.DurationMs, 2),
            write.ObservedLength,
            write.Verified
                ? null
                : "输入未能通过 TextPattern/ValuePattern 回读确认；请用 get_element_text 或截图复核");

    internal void ValidateTextRequest(string text)
    {
        if (text.Length > 100_000)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"type_text 文本过长（{text.Length} 字符，上限 100000）；请分段输入",
                retryable: false,
                stage: "input");
        }

        if (!_host._display.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }
    }

    internal void EnsureForegroundForTyping(TrackedApp app, IntPtr target, bool exact)
    {
        if (!_host._focus.EnsureForeground(app))
        {
            throw new ToolException(
                "E_FOCUS",
                "无法把目标窗口切换到前台，已取消输入以避免打字到用户当前窗口",
                retryable: true,
                hint: "确认没有模态框禁用主窗口；可改用 click 的消息点击或先关闭弹窗",
                stage: "focus");
        }

        if (exact && WindowManager.GetForegroundWindow() != target)
        {
            WindowManager.ForceForeground(target);
        }

        if (exact && WindowManager.GetForegroundWindow() != target)
        {
            throw new ToolException(
                "E_FOCUS",
                "无法把 hwnd 指定的窗口切换到前台，已取消输入",
                new JsonObject { ["hwnd"] = target.ToInt64() },
                retryable: true,
                hint: "确认该对话框未被禁用；必要时改用 click_ref 操作其控件",
                stage: "focus");
        }
    }

    /// <summary>前台切换后再取焦点元素；同进程多窗口时，焦点必须落在当前前台窗口内。</summary>
    internal UiaElementHandle? FocusedElementInForeground(TrackedApp app)
    {
        UiaElementHandle? focused = _host._uia.Uia.GetFocusedElementHandle(app.Pid);

        if (focused is not null &&
            !_host.ElementBelongsToWindow(focused.Info, WindowManager.GetForegroundWindow()))
        {
            UiaElementInfo info = focused.Info;
            focused.Dispose();
            throw new ToolException(
                "E_FOCUS",
                "焦点元素不属于当前前台窗口，已取消输入以避免写错文档",
                new JsonObject
                {
                    ["target_pid"] = app.Pid,
                    ["element"] = ElementPresenter.ToJson(info),
                },
                retryable: true,
                hint: "先 click 目标控件或把目标窗口切到前台",
                stage: "focus");
        }

        return focused;
    }

    internal (TextWriteResult Write, bool PatternUsed, bool UseClipboard) WriteByChannel(
        TrackedApp app, UiaElementHandle? focused, TextWriteRequest request, WriteChannelPlan plan)
    {
        string text = request.Text;
        string mode = (plan.Channel ?? "auto").Trim().ToLowerInvariant();

        if (mode == "pattern")
        {
            return (WriteByPattern(focused, text), true, false);
        }

        bool useClipboard = ResolveClipboardUse(mode, text, text.Any(char.IsSurrogate));

        return useClipboard
            ? (_host.TextOps.WriteTextViaClipboard(focused, text), false, true)
            : (WriteByKeyboard(app, focused, request, plan), false, false);
    }

    private static TextWriteResult WriteByPattern(UiaElementHandle? focused, string text)
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
                "控件不支持可用 ValuePattern",
                new JsonObject { ["failure"] = set.Failure },
                retryable: false,
                hint: "改用 channel=auto 或 channel=clipboard",
                stage: "input");
        }

        return new TextWriteResult(
            text.Length, Chunks: 1, TextSupported: true,
            Verified: string.Equals(set.Value, text, StringComparison.Ordinal),
            ObservedLength: set.Value?.Length,
            InjectMs: 0, DurationMs: 0);
    }

    private static bool ResolveClipboardUse(string mode, string text, bool hasSurrogate)
    {
        bool requested = mode == "clipboard" ||
                         (mode == "auto" && (text.Length >= 200 || hasSurrogate));

        if (!requested || !ClipboardText.HasNonTextFormats())
        {
            return requested;
        }

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

        return false;
    }

    private TextWriteResult WriteByKeyboard(
        TrackedApp app, UiaElementHandle? focused, TextWriteRequest request, WriteChannelPlan plan) =>
        plan.Verify
            ? _host.TextOps.WriteTextVerified(
                app, focused,
                new TextWriteRequest(request.Text, Replace: false, TimeoutMs: 10000,
                    request.ChunkChars, request.VerifyEveryChunks, request.PaceMs))
            : _host.TextOps.WriteTextUnverified(
                app, focused, request.Text, request.PaceMs, request.ChunkChars);

    private static bool IsExplicitTarget(string? target) =>
        (target ?? string.Empty).Trim().ToLowerInvariant() is "current_dialog" or "foreground";

    public PressKeysView PressKeys(
        string keys, bool keepForeground = false, int? pid = null,
        bool allowSystemKeys = false, bool observe = true, long? hwnd = null,
        string? target = null) =>
        PressKeySequence(new[] { keys }, keepForeground, pid, allowSystemKeys, observe, hwnd, target);

    /// <summary>
    /// 在一次前台租约内发送多个按键序列，供菜单导航（Alt+D → Down → Enter 等）使用。
    /// keepForeground=true 时不归还用户前台，避免菜单在后续调用前被关闭。
    /// </summary>
    public PressKeysView PressKeySequence(
        IReadOnlyList<string> keys,
        bool keepForeground = false,
        int? pid = null,
        bool allowSystemKeys = false,
        bool observe = true,
        long? hwnd = null,
        string? target = null)
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
        RequireInteractiveDesktop();

        DisplayInfo display = _host._display.Ensure();
        (TrackedApp app, IntPtr targetHwnd) = _host.RequireInputTarget(pid, hwnd, target);
        BlockingDialogGuard.RequireNotBlocked(targetHwnd, "press_keys");
        _host._display.RequireWindowOnDisplay(display, targetHwnd, "press_keys");
        IntPtr previous = WindowManager.GetForegroundWindow();

        if (keepForeground)
        {
            _host.Lease.Acquire(
                _host._session.Clients.CurrentId,
                app.Pid,
                targetHwnd.ToInt64(),
                ForegroundLease.DefaultTtlMs,
                exclusive: false,
                source: "keep_foreground",
                previousForeground: previous);
        }

        _host._focus.PauseFocusGuard(keepForeground ? ForegroundLease.DefaultTtlMs : 0);

        try
        {
            RequireForeground(
                app, targetHwnd, exact: hwnd is not null || IsExplicitTarget(target));

            try
            {
                var stopwatch = Stopwatch.StartNew();
                ObserveSignature? observeBefore = observe
                    ? _host._display.CaptureSignature(display, display.X, display.Y, display.Width, display.Height)
                    : null;

                double injectMs = InjectSequence(keys, allowSystemKeys);

                ObserveSignature? observeAfter = observeBefore is null
                    ? null
                    : _host._display.CaptureSignature(display, display.X, display.Y, display.Width, display.Height);
                ObservedView? observed = _host._uia.BuildObserved(
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
                if (!keepForeground &&
                    !_host.Lease.IsActive(_host._session.Clients.CurrentId))
                {
                    _host._focus.RestoreForeground(previous);
                }
            }
        }
        finally
        {
            if (!keepForeground &&
                !_host.Lease.IsActive(_host._session.Clients.CurrentId))
            {
                _host._focus.ResumeFocusGuard();
            }
        }
    }

    private void RequireInteractiveDesktop()
    {
        if (_host._display.IsInputDesktopAvailable())
        {
            return;
        }

        throw new ToolException(
            "E_NO_INTERACTIVE_DESKTOP",
            "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
            retryable: true,
            hint: "解锁桌面或改用 UIA 语义操作",
            stage: "input");
    }

    private void RequireForeground(TrackedApp app, IntPtr target, bool exact)
    {
        if (!_host._focus.EnsureForeground(app))
        {
            throw new ToolException(
                "E_FOCUS",
                "无法把目标窗口切换到前台，已取消按键以避免输入到用户当前窗口",
                retryable: true,
                hint: "确认没有模态框禁用主窗口；可改用消息点击或先关闭弹窗",
                stage: "focus");
        }

        if (exact && WindowManager.GetForegroundWindow() != target)
        {
            WindowManager.ForceForeground(target);
        }

        if (exact && WindowManager.GetForegroundWindow() != target)
        {
            throw new ToolException(
                "E_FOCUS",
                "无法把 hwnd 指定的窗口切换到前台，已取消按键",
                new JsonObject { ["hwnd"] = target.ToInt64() },
                retryable: true,
                hint: "确认该对话框未被禁用；必要时改用 click_ref 操作其控件",
                stage: "focus");
        }
    }

    private double InjectSequence(IReadOnlyList<string> keys, bool allowSystemKeys)
    {
        double injectMs = 0;
        int sent = 0;

        foreach (string key in keys)
        {
            if (_host._session.Cancellation().IsCancellationRequested)
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

        return injectMs;
    }
}
