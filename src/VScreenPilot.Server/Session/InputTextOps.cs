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
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>文本写入请求。</summary>
internal sealed record TextWriteRequest(
    string Text, bool Replace, int TimeoutMs, int ChunkChars = 32, int VerifyEveryChunks = 1,
    int PaceMs = 0);

/// <summary>TextOps：InputRouter 的输入子通道。</summary>
internal sealed class InputTextOps
{
    internal readonly InputRouter _host;

    public InputTextOps(InputRouter host) => _host = host;
    /// <summary>
    /// 把文本分块写入当前前台目标，并在每块后回读确认。
    /// 关键点：只有确认完成后调用方才允许归还前台，避免剩余按键落到用户窗口。
    /// </summary>
    public TextWriteResult WriteTextVerified(
        TrackedApp app, UiaElementHandle? target, TextWriteRequest request)
    {
        string text = request.Text;
        bool replace = request.Replace;
        int timeoutMs = request.TimeoutMs;
        int chunkChars = request.ChunkChars;
        int verifyEveryChunks = request.VerifyEveryChunks;
        string? before = target is null ? null : _host._uia.Uia.GetText(target.Element, 65536);
        bool textSupported = before is not null;
        var stopwatch = Stopwatch.StartNew();
        double injectMs = 0;
        int typed = 0;
        int chunks = 0;
        var sentBuilder = new StringBuilder(text.Length);

        foreach (string chunk in SplitTextChunks(text, chunkChars))
        {
            EnsureTypingContinues(app, typed, chunks);

            var chunkWatch = Stopwatch.StartNew();
            typed += InputInjector.TypeText(chunk);
            injectMs += chunkWatch.Elapsed.TotalMilliseconds;
            sentBuilder.Append(chunk);
            chunks++;
            ToolProgress.Report(
                Math.Min(sentBuilder.Length, text.Length),
                Math.Max(1, text.Length),
                $"type_text 已输入 {sentBuilder.Length}/{text.Length}");

            // 自绘控件/旧式网格经常只吃掉第一块的一部分；pace_ms 让每个块之间有可调间隔。
            // 最后一块不必再等，避免无意义的尾部延迟。
            if (request.PaceMs > 0 && sentBuilder.Length < text.Length)
            {
                Thread.Sleep(Math.Clamp(request.PaceMs, 1, 2000));
            }

            // 长文本逐块读全文是 O(n²)；每 N 块确认一次，最后再整体确认。
            if (textSupported && target is not null)
            {
                if (chunks % Math.Max(1, verifyEveryChunks) == 0 &&
                    !WaitForChunk(target, before!, sentBuilder.ToString(), replace, timeoutMs, stopwatch))
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
            else
            {
                // 没有 TextPattern/ValuePattern 时退回保守节流；无法精确确认。
                Thread.Sleep(Math.Clamp(25 + chunk.Length * 2, 35, 80));
            }
        }

        // 不支持回读时至少给应用留出消费时间，并确认前台仍然属于目标。
        if (!textSupported)
        {
            SettleWithoutTextSupport(app, text.Length, typed);
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

    private void EnsureTypingContinues(TrackedApp app, int typed, int chunks)
    {
        if (_host._session.Cancellation().IsCancellationRequested)
        {
            throw new ToolException(
                "E_CANCELLED",
                "type_text 已被取消，停止继续注入",
                new JsonObject { ["typed"] = typed, ["chunks"] = chunks },
                retryable: true,
                hint: "重新调用 type_text；已输入的内容不会自动撤销",
                stage: "input");
        }

        if (!_host._focus.IsAppForeground(app))
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
    }

    private bool WaitForChunk(
        UiaElementHandle target,
        string before,
        string sent,
        bool replace,
        int timeoutMs,
        Stopwatch stopwatch)
    {
        int remaining = Math.Max(0, timeoutMs - (int)stopwatch.ElapsedMilliseconds);
        int chunkTimeout = Math.Clamp(500 + sent.Length * 8, 500, Math.Max(500, remaining));
        return WaitForText(target, before, sent, replace, chunkTimeout);
    }

    private void SettleWithoutTextSupport(TrackedApp app, int textLength, int typed)
    {
        int settle = Math.Clamp(120 + textLength * 4, 180, 1500);
        var deadline = DateTime.UtcNow.AddMilliseconds(settle);

        while (DateTime.UtcNow < deadline)
        {
            if (!_host._focus.IsAppForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS_LOST",
                    "输入结束前目标窗口失去前台，请用 get_element_text/截图复核",
                    new JsonObject { ["target_pid"] = app.Pid, ["typed"] = typed });
            }

            Thread.Sleep(20);
        }
    }

    internal bool WaitForText(
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

    internal bool TextMatches(string before, string after, string sent, bool replace)
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

    internal string NormalizeForCompare(string value) =>
        value.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>快速路径：一次 SendInput 写完，不做逐块回读；调用方需自行复核。</summary>
    /// <summary>
    /// 剪贴板粘贴：一次性写入，避免 O(n) 的 SendInput 与 IME 组合问题。
    /// 会保存/还原原有文本剪贴板；非文本内容无法还原，因此只在长文本或显式要求时使用。
    /// </summary>
    internal TextWriteResult WriteTextViaClipboard(UiaElementHandle? target, string text)
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
            LogSink.Debug($"忽略异常: {exception.Message}");
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
                LogSink.Debug($"忽略异常: {exception.Message}");
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

    internal TextWriteResult WriteTextUnverified(
        TrackedApp app,
        UiaElementHandle? target,
        string text,
        int paceMs = 0,
        int chunkChars = 32)
    {
        string? before = target is null ? null : _host._uia.Uia.GetText(target.Element, 65536);
        var stopwatch = Stopwatch.StartNew();
        double injectMs = 0;
        int typed = 0;
        int chunks = 0;

        if (paceMs <= 0 || text.Length <= chunkChars)
        {
            if (!_host._focus.IsAppForeground(app))
            {
                throw new ToolException(
                    "E_FOCUS_LOST",
                    "输入前目标窗口失去前台，已中止",
                    retryable: true,
                    stage: "focus");
            }

            var injectWatch = Stopwatch.StartNew();
            typed = InputInjector.TypeText(text);
            injectMs = injectWatch.Elapsed.TotalMilliseconds;
            chunks = text.Length == 0 ? 0 : 1;
        }
        else
        {
            // 未验证路径也要遵守 pace_ms：自绘控件按块输入时，块间停顿是必要的。
            int effectiveChunk = Math.Clamp(chunkChars, 1, 256);
            int effectivePace = Math.Clamp(paceMs, 1, 2000);
            List<string> parts = SplitTextChunks(text, effectiveChunk).ToList();

            foreach (string chunk in parts)
            {
                if (!_host._focus.IsAppForeground(app))
                {
                    throw new ToolException(
                        "E_FOCUS_LOST",
                        "输入过程中目标窗口失去前台，已中止",
                        new JsonObject { ["typed"] = typed, ["chunks"] = chunks },
                        retryable: true,
                        stage: "focus");
                }

                var chunkWatch = Stopwatch.StartNew();
                typed += InputInjector.TypeText(chunk);
                injectMs += chunkWatch.Elapsed.TotalMilliseconds;
                chunks++;

                if (chunks < parts.Count)
                {
                    Thread.Sleep(effectivePace);
                }
            }
        }

        Thread.Sleep(Math.Clamp(120 + text.Length * 2, 200, 1500));

        string? after = target is null ? null : target.GetText(65536);
        return new TextWriteResult(
            typed,
            Chunks: chunks,
            TextSupported: before is not null,
            Verified: false,
            ObservedLength: after?.Length,
            InjectMs: injectMs,
            DurationMs: stopwatch.Elapsed.TotalMilliseconds);
    }

    internal IEnumerable<string> SplitTextChunks(string text, int chunkChars)
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
}
