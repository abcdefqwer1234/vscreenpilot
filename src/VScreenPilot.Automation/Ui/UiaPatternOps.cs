using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;

namespace VScreenPilot.Uia;

/// <summary>Patterns：UiaService 的拆分子集。</summary>
internal sealed class UiaPatternOps
{
    private readonly UiaService _core;

    public UiaPatternOps(UiaService core) => _core = core;
    /// <summary>
    /// 在指定屏幕坐标尝试 UIA 的 InvokePattern；成功则不需要移动光标。
    /// </summary>
    public bool TryInvokeAtPoint(
        int x, int y, int? expectedPid, out UiaElementInfo? info,
        Func<UiaElementInfo, bool>? confirm = null)
    {
        lock (_core._sync)
        {
            info = null;
            IUIAutomationElement? element = null;
            object? pattern = null;

            try
            {
                var point = new tagPOINT { x = x, y = y };
                element = _core._automation.ElementFromPoint(point);
                if (element is null)
                {
                    return false;
                }

                info = _core.Factory.Describe(element, cached: false);

                // 点上的元素可能属于覆盖在上面的其他进程（宿主窗口、WebView 等）。
                // 只对本会话的目标进程执行 UIA Invoke，否则交回消息注入路径。
                if (expectedPid.HasValue &&
                    (info is null || info.ProcessId != expectedPid.Value))
                {
                    return false;
                }

                // 破坏性目标护栏：调用方可在真正 Invoke 前拒绝该元素。
                // 回调抛出的 ToolException 会原样穿透，不会被“不支持”吞掉。
                if (info is not null && confirm is not null && !confirm(info))
                {
                    return false;
                }

                pattern = element.GetCurrentPattern(UiaService.InvokePatternId);
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
                UiaElementFactoryOps.ReleaseElement(pattern);
                UiaElementFactoryOps.ReleaseElement(element);
            }

            return false;
        }
    }

    /// <summary>
    /// 只读命中测试：返回屏幕坐标下的 UIA 元素，不触发任何 pattern。
    /// 用于点击前报告 hit_element，避免“点到了什么”只能靠猜。
    /// </summary>
    public UiaElementInfo? DescribeAtPoint(int x, int y, int? expectedPid)
    {
        lock (_core._sync)
        {
            IUIAutomationElement? element = null;

            try
            {
                element = _core._automation.ElementFromPoint(new tagPOINT { x = x, y = y });
                if (element is null)
                {
                    return null;
                }

                UiaElementInfo? info = _core.Factory.Describe(element, cached: false);

                if (info is null)
                {
                    return null;
                }

                // 命中测试同样必须限定在本会话目标进程，避免读到覆盖层的其他应用。
                if (expectedPid.HasValue && info.ProcessId != expectedPid.Value)
                {
                    return null;
                }

                return info;
            }
            catch (COMException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(element);
            }
        }
    }

    /* ------------------------------------------------------------------ */
    /* 语义动作                                                            */
    /* ------------------------------------------------------------------ */

    public void FocusElement(IUIAutomationElement element)
    {
        lock (_core._sync)
        {
            element.SetFocus();
        }
    }

    /// <summary>按指定模式触发；Auto 依次尝试 Invoke → Toggle → SelectionItem → ExpandCollapse → Legacy。</summary>
    public (bool Ok, string? Strategy) TryInvoke(
        IUIAutomationElement element, UiaInvokePattern pattern = UiaInvokePattern.Auto)
    {
        lock (_core._sync)
        {
            switch (pattern)
            {
                case UiaInvokePattern.Invoke:
                    return TryWithPattern<IUIAutomationInvokePattern>(
                        element, UiaService.InvokePatternId, item => item.Invoke(), "Invoke");

                case UiaInvokePattern.Toggle:
                    return TryWithPattern<IUIAutomationTogglePattern>(
                        element, UiaService.TogglePatternId, item => item.Toggle(), "Toggle");

                case UiaInvokePattern.Select:
                    return TryWithPattern<IUIAutomationSelectionItemPattern>(
                        element, UiaService.SelectionItemPatternId, item => item.Select(), "SelectionItem");

                case UiaInvokePattern.Expand:
                    return TryWithPattern<IUIAutomationExpandCollapsePattern>(
                        element, UiaService.ExpandCollapsePatternId, item => item.Expand(), "ExpandCollapse");

                case UiaInvokePattern.Legacy:
                    return TryLegacyInvoke(element);

                default:
                    (bool Ok, string? Strategy) result = TryWithPattern<IUIAutomationInvokePattern>(
                        element, UiaService.InvokePatternId, item => item.Invoke(), "Invoke");

                    if (result.Ok)
                    {
                        return result;
                    }

                    result = TryWithPattern<IUIAutomationTogglePattern>(
                        element, UiaService.TogglePatternId, item => item.Toggle(), "Toggle");

                    if (result.Ok)
                    {
                        return result;
                    }

                    result = TryWithPattern<IUIAutomationSelectionItemPattern>(
                        element, UiaService.SelectionItemPatternId, item => item.Select(), "SelectionItem");

                    if (result.Ok)
                    {
                        return result;
                    }

                    result = TryWithPattern<IUIAutomationExpandCollapsePattern>(
                        element, UiaService.ExpandCollapsePatternId, item => item.Expand(), "ExpandCollapse");

                    return result.Ok ? result : TryLegacyInvoke(element);
            }
        }
    }

    /// <summary>滚动到可见；不支持 ScrollItem 的控件返回 false，由调用方保持原行为。</summary>
    internal (bool Ok, string? Strategy) TryScrollIntoView(IUIAutomationElement element) =>
        TryWithPattern<IUIAutomationScrollItemPattern>(
            element, UiaService.ScrollItemPatternId, pattern => pattern.ScrollIntoView(), "ScrollItem");

    internal (bool Ok, string? Strategy) TryLegacyInvoke(IUIAutomationElement element) =>
        TryWithPattern<IUIAutomationLegacyIAccessiblePattern>(
            element,
            UiaService.LegacyIAccessiblePatternId,
            pattern =>
            {
                if (string.IsNullOrEmpty(pattern.CurrentDefaultAction))
                {
                    throw new InvalidOperationException("no default action");
                }

                pattern.DoDefaultAction();
            },
            "LegacyIAccessible");

    internal (bool Ok, string? Strategy) TryWithPattern<T>(
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
            UiaElementFactoryOps.ReleaseElement(pattern);
        }
    }

    /// <summary>通过 ValuePattern 写值；Failure 非空表示写值调用本身失败。</summary>
    public UiaSetValueResult TrySetValue(IUIAutomationElement element, string value)
    {
        lock (_core._sync)
        {
            object? pattern = null;

            try
            {
                try
                {
                    pattern = element.GetCurrentPattern(UiaService.ValuePatternId);
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
                UiaElementFactoryOps.ReleaseElement(pattern);
            }
        }
    }

    internal string? ReadValuePattern(IUIAutomationValuePattern pattern)
    {
        try
        {
            return _core.TextOps.SafeString(pattern.CurrentValue);
        }
        catch (COMException)
        {
            // 写入可能成功，但 provider 不支持回读。
            return null;
        }
    }
}
