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

internal sealed partial class InputRouter
{
    public TypeTextView TypeText(
        string text, int? pid = null, bool verify = true, int chunkChars = 32, string? channel = null)
    {
        ValidateTextRequest(text);

        DisplayInfo display = _display.Ensure();
        TrackedApp app = RequireTrackedApp(pid);
        _display.RequireWindowOnDisplay(display, app.Hwnd, "type_text");
        using var inputLease = InputLease.TryAcquire(3000, "type_text");
        IntPtr previous = WindowManager.GetForegroundWindow();
        _focus.PauseFocusGuard(0);
        int effectiveChunk = Math.Clamp(chunkChars, 1, 256);
        int verifyEvery = text.Length > 20000 ? 16 : text.Length > 2048 ? 4 : 1;

        try
        {
            EnsureForegroundForTyping(app);

            try
            {
                using UiaElementHandle? focused = FocusedElementInForeground(app);

                (TextWriteResult write, bool patternUsed, bool useClipboard) = WriteByChannel(
                    app, focused, text, channel, verify, effectiveChunk, verifyEvery);

                // 写入前焦点控件偶发不可用（UWP/新版记事本）；写完后再取一次补校验。
                // 避免“内容其实写对了，但 verified 永远是 false”。
                if (!write.Verified && verify && focused is null && !patternUsed && !useClipboard)
                {
                    using UiaElementHandle? late = _uia.Uia.GetFocusedElementHandle(app.Pid);
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
                _focus.RestoreForeground(previous);
            }
        }
        finally
        {
            _focus.ResumeFocusGuard();
        }
    }

    private void ValidateTextRequest(string text)
    {
        if (text.Length > 100_000)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"type_text 文本过长（{text.Length} 字符，上限 100000）；请分段输入",
                retryable: false,
                stage: "input");
        }

        if (!_display.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }
    }

    private void EnsureForegroundForTyping(TrackedApp app)
    {
        if (!_focus.EnsureForeground(app))
        {
            throw new ToolException(
                "E_FOCUS",
                "无法把目标窗口切换到前台，已取消输入以避免打字到用户当前窗口",
                retryable: true,
                hint: "确认没有模态框禁用主窗口；可改用 click 的消息点击或先关闭弹窗",
                stage: "focus");
        }
    }

    /// <summary>前台切换后再取焦点元素；同进程多窗口时，焦点必须落在当前前台窗口内。</summary>
    private UiaElementHandle? FocusedElementInForeground(TrackedApp app)
    {
        UiaElementHandle? focused = _uia.Uia.GetFocusedElementHandle(app.Pid);

        if (focused is not null &&
            !ElementBelongsToWindow(focused.Info, WindowManager.GetForegroundWindow()))
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

    private (TextWriteResult Write, bool PatternUsed, bool UseClipboard) WriteByChannel(
        TrackedApp app, UiaElementHandle? focused, string text, string? channel,
        bool verify, int effectiveChunk, int verifyEvery)
    {
        string mode = (channel ?? "auto").Trim().ToLowerInvariant();
        bool hasSurrogate = text.Any(char.IsSurrogate);
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
                    "控件不支持可用 ValuePattern",
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

        return (write, patternUsed, useClipboard);
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

        if (!_display.IsInputDesktopAvailable())
        {
            throw new ToolException(
                "E_NO_INTERACTIVE_DESKTOP",
                "当前没有可交互桌面（锁屏/安全桌面/Session 0），SendInput 无法注入",
                retryable: true,
                hint: "解锁桌面或改用 UIA 语义操作",
                stage: "input");
        }

        DisplayInfo display = _display.Ensure();
        TrackedApp app = RequireTrackedApp(pid);
        _display.RequireWindowOnDisplay(display, app.Hwnd, "press_keys");
        IntPtr previous = WindowManager.GetForegroundWindow();
        _focus.PauseFocusGuard(keepForeground ? 30000 : 0);

        try
        {
            if (!_focus.EnsureForeground(app))
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
                    ? _display.CaptureSignature(display, display.X, display.Y, display.Width, display.Height)
                    : null;

                int sent = 0;

                foreach (string key in keys)
                {
                    if (_session.Cancellation().IsCancellationRequested)
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
                    : _display.CaptureSignature(display, display.X, display.Y, display.Width, display.Height);
                ObservedView? observed = _uia.BuildObserved(
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
                    _focus.RestoreForeground(previous);
                }
            }
        }
        finally
        {
            if (!keepForeground)
            {
                _focus.ResumeFocusGuard();
            }
        }
    }

}
