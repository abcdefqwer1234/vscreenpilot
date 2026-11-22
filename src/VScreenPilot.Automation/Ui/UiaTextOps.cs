using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Uia;

/// <summary>Text：UiaService 的拆分子集。</summary>
internal sealed class UiaTextOps
{
    private readonly UiaService _core;

    public UiaTextOps(UiaService core) => _core = core;
    /// <summary>读取 ValuePattern 的当前值；不支持时返回 null。</summary>
    public string? GetValue(IUIAutomationElement element)
    {
        lock (_core._sync)
        {
            object? pattern = null;

            try
            {
                pattern = element.GetCurrentPattern(UiaService.ValuePatternId);
                return pattern is IUIAutomationValuePattern valuePattern
                    ? SafeString(valuePattern.CurrentValue)
                    : null;
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(pattern);
            }
        }
    }

    /// <summary>
    /// 读取文本控件正文：优先 TextPattern，其次 ValuePattern。
    /// Word 正文（Edit/Body）通常只有 TextPattern，没有 ValuePattern。
    /// </summary>
    public string? GetText(IUIAutomationElement element, int maxChars = 65536)
    {
        lock (_core._sync)
        {
            object? pattern = null;
            object? range = null;

            try
            {
                pattern = element.GetCurrentPattern(UiaService.TextPatternId);

                if (pattern is IUIAutomationTextPattern textPattern)
                {
                    range = textPattern.DocumentRange;

                    if (range is IUIAutomationTextRange textRange)
                    {
                        return NormalizeText(textRange.GetText(maxChars));
                    }
                }
            }
            catch (Exception exception) when (
                exception is COMException or ArgumentException or InvalidOperationException)
            {
                // 不是所有 provider 都支持 TextPattern；继续尝试 ValuePattern。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(range);
                UiaElementFactoryOps.ReleaseElement(pattern);
            }

            return GetValue(element);
        }
    }

    /// <summary>返回当前焦点元素句柄；调用方负责 _core.Dispose。expectedPid 用于拒绝焦点落在其他进程。</summary>
    public UiaElementHandle? GetFocusedElementHandle(int? expectedPid = null)
    {
        IUIAutomationElement? element = null;

        try
        {
            element = _core._automation.GetFocusedElementBuildCache(_core._elementCache);

            if (element is null)
            {
                return null;
            }

            UiaElementInfo? info = _core.Factory.Describe(element, cached: true);

            if (info is null || (info.Width == 0 && info.Height == 0 && info.Name.Length == 0))
            {
                info = _core.Factory.DescribeCurrent(element) ?? info;
            }

            if (info is null)
            {
                return null;
            }

            if (expectedPid.HasValue && info.ProcessId != expectedPid.Value)
            {
                return null;
            }

            var handle = new UiaElementHandle(_core, element, info);
            element = null;
            return handle;
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
        finally
        {
            UiaElementFactoryOps.ReleaseElement(element);
        }
    }

    internal string NormalizeText(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n").Replace('\r', '\n');

    internal string ReadText(IUIAutomationElement element, int controlType)
    {
        if (controlType is not (50004 or 50030))
        {
            return string.Empty;
        }

        object? pattern = null;
        object? range = null;

        try
        {
            pattern = element.GetCurrentPattern(UiaService.TextPatternId);

            if (pattern is IUIAutomationTextPattern textPattern)
            {
                range = textPattern.DocumentRange;

                if (range is IUIAutomationTextRange textRange)
                {
                    // 树/查找结果只带摘要，避免把整篇文档塞进 JSON。
                    string text = NormalizeText(textRange.GetText(2048));
                    return text.Length >= 2048 ? text + "\n...[truncated]" : text;
                }
            }
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
        finally
        {
            UiaElementFactoryOps.ReleaseElement(range);
            UiaElementFactoryOps.ReleaseElement(pattern);
        }

        return string.Empty;
    }

    internal string ReadTextCurrent(IUIAutomationElement element)
    {
        object? pattern = null;
        object? range = null;

        try
        {
            pattern = element.GetCurrentPattern(UiaService.TextPatternId);

            if (pattern is IUIAutomationTextPattern textPattern)
            {
                range = textPattern.DocumentRange;

                if (range is IUIAutomationTextRange textRange)
                {
                    string text = NormalizeText(textRange.GetText(2048));
                    return text.Length >= 2048 ? text + "\n...[truncated]" : text;
                }
            }
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
        finally
        {
            UiaElementFactoryOps.ReleaseElement(range);
            UiaElementFactoryOps.ReleaseElement(pattern);
        }

        return string.Empty;
    }

    internal string? SafeString(string? value) => value;

    /* ------------------------------------------------------------------ */
    /* 语义树                                                              */
    /* ------------------------------------------------------------------ */
}
