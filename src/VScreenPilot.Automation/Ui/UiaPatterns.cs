using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;

namespace VScreenPilot.Uia;

internal sealed partial class UiaService
{
    /// <summary>
    /// 在指定屏幕坐标尝试 UIA 的 InvokePattern；成功则不需要移动光标。
    /// </summary>
    public bool TryInvokeAtPoint(int x, int y, int? expectedPid, out UiaElementInfo? info)
    {
        lock (_sync)
        {
        info = null;
        IUIAutomationElement? element = null;
        object? pattern = null;

        try
        {
            var point = new tagPOINT { x = x, y = y };
            element = _automation.ElementFromPoint(point);
            if (element is null)
            {
                return false;
            }

            info = Describe(element, cached: false);

            // 点上的元素可能属于覆盖在上面的其他进程（宿主窗口、WebView 等）。
            // 只对本会话的目标进程执行 UIA Invoke，否则交回消息注入路径。
            if (expectedPid.HasValue &&
                (info is null || info.ProcessId != expectedPid.Value))
            {
                return false;
            }

            pattern = element.GetCurrentPattern(InvokePatternId);
            if (pattern is IUIAutomationInvokePattern invoke)
            {
                invoke.Invoke();
                return true;
            }
        }
        catch (COMException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
        finally
        {
            ReleaseElement(pattern);
            ReleaseElement(element);
        }

        return false;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 语义动作                                                            */
    /* ------------------------------------------------------------------ */

    public void FocusElement(IUIAutomationElement element)
    {
        lock (_sync)
        {
            element.SetFocus();
        }
    }

    /// <summary>按指定模式触发；Auto 依次尝试 Invoke → Toggle → SelectionItem → ExpandCollapse → Legacy。</summary>
    public (bool Ok, string? Strategy) TryInvoke(
        IUIAutomationElement element, UiaInvokePattern pattern = UiaInvokePattern.Auto)
    {
        lock (_sync)
        {
        switch (pattern)
        {
            case UiaInvokePattern.Invoke:
                return TryWithPattern<IUIAutomationInvokePattern>(
                    element, InvokePatternId, item => item.Invoke(), "Invoke");

            case UiaInvokePattern.Toggle:
                return TryWithPattern<IUIAutomationTogglePattern>(
                    element, TogglePatternId, item => item.Toggle(), "Toggle");

            case UiaInvokePattern.Select:
                return TryWithPattern<IUIAutomationSelectionItemPattern>(
                    element, SelectionItemPatternId, item => item.Select(), "SelectionItem");

            case UiaInvokePattern.Expand:
                return TryWithPattern<IUIAutomationExpandCollapsePattern>(
                    element, ExpandCollapsePatternId, item => item.Expand(), "ExpandCollapse");

            case UiaInvokePattern.Legacy:
                return TryLegacyInvoke(element);

            default:
                (bool Ok, string? Strategy) result = TryWithPattern<IUIAutomationInvokePattern>(
                    element, InvokePatternId, item => item.Invoke(), "Invoke");

                if (result.Ok)
                {
                    return result;
                }

                result = TryWithPattern<IUIAutomationTogglePattern>(
                    element, TogglePatternId, item => item.Toggle(), "Toggle");

                if (result.Ok)
                {
                    return result;
                }

                result = TryWithPattern<IUIAutomationSelectionItemPattern>(
                    element, SelectionItemPatternId, item => item.Select(), "SelectionItem");

                if (result.Ok)
                {
                    return result;
                }

                result = TryWithPattern<IUIAutomationExpandCollapsePattern>(
                    element, ExpandCollapsePatternId, item => item.Expand(), "ExpandCollapse");

                return result.Ok ? result : TryLegacyInvoke(element);
        }
        }
    }

    private static (bool Ok, string? Strategy) TryLegacyInvoke(IUIAutomationElement element) =>
        TryWithPattern<IUIAutomationLegacyIAccessiblePattern>(
            element,
            LegacyIAccessiblePatternId,
            pattern =>
            {
                if (string.IsNullOrEmpty(pattern.CurrentDefaultAction))
                {
                    throw new InvalidOperationException("no default action");
                }

                pattern.DoDefaultAction();
            },
            "LegacyIAccessible");

    private static (bool Ok, string? Strategy) TryWithPattern<T>(
        IUIAutomationElement element, int patternId, Action<T> action, string strategy)
        where T : class
    {
        object? pattern = null;

        try
        {
            pattern = element.GetCurrentPattern(patternId);
            if (pattern is not T typed)
            {
                return (false, null);
            }

            action(typed);
            return (true, strategy);
        }
        catch (COMException)
        {
            return (false, null);
        }
        catch (InvalidCastException)
        {
            return (false, null);
        }
        catch (InvalidOperationException)
        {
            return (false, null);
        }
        finally
        {
            ReleaseElement(pattern);
        }
    }

    /// <summary>通过 ValuePattern 写值；Failure 非空表示写值调用本身失败。</summary>
    public UiaSetValueResult TrySetValue(IUIAutomationElement element, string value)
    {
        lock (_sync)
        {
        object? pattern = null;

        try
        {
            try
            {
                pattern = element.GetCurrentPattern(ValuePatternId);
            }
            catch (COMException)
            {
                return new UiaSetValueResult(false, false, null, null);
            }

            if (pattern is not IUIAutomationValuePattern valuePattern)
            {
                return new UiaSetValueResult(false, false, null, null);
            }

            bool readOnly;

            try
            {
                readOnly = valuePattern.CurrentIsReadOnly != 0;
            }
            catch (COMException)
            {
                readOnly = false;
            }

            if (readOnly)
            {
                return new UiaSetValueResult(true, true, ReadValuePattern(valuePattern), null);
            }

            try
            {
                valuePattern.SetValue(value);
            }
            catch (COMException exception)
            {
                return new UiaSetValueResult(
                    false, false, null, $"0x{exception.HResult:X8} {exception.Message}");
            }

            return new UiaSetValueResult(true, false, ReadValuePattern(valuePattern), null);
        }
        finally
        {
            ReleaseElement(pattern);
        }
        }
    }

    private static string? ReadValuePattern(IUIAutomationValuePattern pattern)
    {
        try
        {
            return SafeString(pattern.CurrentValue);
        }
        catch (COMException)
        {
            // 写入可能成功，但 provider 不支持回读。
            return null;
        }
    }
}
