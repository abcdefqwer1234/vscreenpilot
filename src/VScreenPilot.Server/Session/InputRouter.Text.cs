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
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
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
                System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
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

}
