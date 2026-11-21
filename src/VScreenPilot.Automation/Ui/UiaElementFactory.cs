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
    private static bool IsUsable(UiaElementInfo? info) =>
        info is not null && !info.Offscreen && info.Width > 0 && info.Height > 0;

    private static UiaElementInfo? Describe(IUIAutomationElement? element, bool cached)
    {
        if (element is null)
        {
            return null;
        }

        if (cached)
        {
            UiaElementInfo? info = null;

            try
            {
                info = DescribeCached(element);
            }
            catch (Exception exception) when (
                exception is COMException or ArgumentException or InvalidOperationException)
            {
                // 某些 provider/未走 BuildCache 的元素不支持缓存，退回实时读取。
            }

            // runtime_id 是元素引用的基础；缺失时用实时读取补全。
            if (info is not null && !string.IsNullOrEmpty(info.RuntimeId))
            {
                return info;
            }

            if (info is not null)
            {
                return DescribeCurrent(element) ?? info;
            }
        }

        return DescribeCurrent(element);
    }

    private static UiaElementInfo? DescribeCached(IUIAutomationElement element)
    {
        tagRECT rect = element.CachedBoundingRectangle;
        int controlType = element.CachedControlType;
        int[] runtime = GetCachedRuntimeId(element);
        bool password = element.CachedIsPassword != 0;

        return new UiaElementInfo(
            string.Join(".", runtime),
            element.CachedName ?? string.Empty,
            element.CachedAutomationId ?? string.Empty,
            controlType,
            ControlTypeName(controlType),
            rect.left,
            rect.top,
            rect.right,
            rect.bottom,
            element.CachedIsEnabled != 0,
            element.CachedIsOffscreen != 0,
            element.CachedProcessId,
            element.CachedClassName ?? string.Empty,
            Focusable: element.CachedIsKeyboardFocusable != 0,
            Focused: element.CachedHasKeyboardFocus != 0,
            Password: password,
            Value: password ? string.Empty : ReadValue(element, controlType),
            Text: password ? string.Empty : ReadText(element, controlType));
    }

    private static UiaElementInfo? DescribeCurrent(IUIAutomationElement? element)
    {
        if (element is null)
        {
            return null;
        }

        try
        {
            tagRECT rect = element.CurrentBoundingRectangle;
            int[] runtime = element.GetRuntimeId() ?? Array.Empty<int>();
            int controlType = element.CurrentControlType;
            bool password = element.CurrentIsPassword != 0;

            return new UiaElementInfo(
                string.Join(".", runtime),
                element.CurrentName ?? string.Empty,
                element.CurrentAutomationId ?? string.Empty,
                controlType,
                ControlTypeName(controlType),
                rect.left,
                rect.top,
                rect.right,
                rect.bottom,
                element.CurrentIsEnabled != 0,
                element.CurrentIsOffscreen != 0,
                element.CurrentProcessId,
                element.CurrentClassName ?? string.Empty,
                Focusable: element.CurrentIsKeyboardFocusable != 0,
                Focused: element.CurrentHasKeyboardFocus != 0,
                Password: password,
                Value: password ? string.Empty : SafeCurrentValue(element),
                Text: password ? string.Empty : ReadTextCurrent(element));
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string ReadValue(IUIAutomationElement element, int controlType)
    {
        // 只有文本类控件才值得为 value 付一次跨进程读取；其他控件直接省略。
        if (controlType is not (50004 or 50030))
        {
            return string.Empty;
        }

        try
        {
            object? value = element.GetCurrentPropertyValue(ValuePropertyId);
            return value switch
            {
                null => string.Empty,
                string text => text,
                _ => value.ToString() ?? string.Empty,
            };
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static string SafeCurrentValue(IUIAutomationElement element)
    {
        try
        {
            object? value = element.GetCurrentPropertyValue(ValuePropertyId);
            return value switch
            {
                null => string.Empty,
                string text => text,
                _ => value.ToString() ?? string.Empty,
            };
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private static int[] GetCachedRuntimeId(IUIAutomationElement element)
    {
        try
        {
            return element.GetCachedPropertyValue(RuntimeIdPropertyId) as int[] ?? Array.Empty<int>();
        }
        catch (Exception exception) when (
            exception is COMException or ArgumentException or InvalidOperationException)
        {
            return Array.Empty<int>();
        }
    }

    internal static void ReleaseElement(object? comObject)
    {
        if (comObject is null || !Marshal.IsComObject(comObject))
        {
            return;
        }

        try
        {
            Marshal.ReleaseComObject(comObject);
        }
        catch (Exception exception)
        {
            // 释放失败不影响结果，交给 GC 兜底。
            _ = exception;
        }
    }

    private static bool TryParseRuntimeId(string value, out int[] ids)
    {
        string[] parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        ids = new int[parts.Length];

        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out ids[i]))
            {
                ids = Array.Empty<int>();
                return false;
            }
        }

        return ids.Length > 0;
    }

    public static bool IsKnownControlType(string value) => TryParseControlType(value, out _);

    public static IReadOnlyList<string> SupportedControlTypes =>
        ControlTypes.Values.OrderBy(value => value, StringComparer.Ordinal).ToList();

    private static bool TryParseControlType(string value, out int controlTypeId)
    {
        if (int.TryParse(value, out controlTypeId))
        {
            return true;
        }

        foreach (var pair in ControlTypes)
        {
            if (pair.Value.Equals(value, StringComparison.OrdinalIgnoreCase))
            {
                controlTypeId = pair.Key;
                return true;
            }
        }

        controlTypeId = 0;
        return false;
    }
}
