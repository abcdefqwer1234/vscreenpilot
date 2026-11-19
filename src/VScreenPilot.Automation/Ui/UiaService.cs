using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;
using VScreenPilot.Mcp;

namespace VScreenPilot.Uia;

internal sealed record UiaElementInfo(
    string RuntimeId,
    string Name,
    string AutomationId,
    int ControlType,
    string ControlTypeName,
    int Left,
    int Top,
    int Right,
    int Bottom,
    bool Enabled,
    bool Offscreen,
    int ProcessId,
    string ClassName,
    bool Focusable = false,
    bool Focused = false,
    bool Password = false,
    string Value = "",
    string Text = "")
{
    public int Width => Math.Max(0, Right - Left);

    public int Height => Math.Max(0, Bottom - Top);

    /// <summary>面向模型的精简表示：只保留定位/判断所需字段，省略 runtime_id/class_name 等诊断信息。</summary>
    public JsonObject ToCompactJson()
    {
        var json = new JsonObject
        {
            ["name"] = Name,
            ["automation_id"] = AutomationId,
            ["control_type"] = ControlTypeName,
            ["bounds"] = new JsonObject
            {
                ["x"] = Left,
                ["y"] = Top,
                ["width"] = Width,
                ["height"] = Height,
            },
        };

        if (!Enabled)
        {
            json["enabled"] = false;
        }

        if (Offscreen)
        {
            json["offscreen"] = true;
        }

        if (Focused)
        {
            json["focused"] = true;
        }

        if (!Password && !string.IsNullOrEmpty(Value))
        {
            json["value"] = Value.Length > 200 ? Value[..200] : Value;
        }

        return json;
    }

    public JsonObject ToJson()
    {
        var json = new JsonObject
        {
            ["runtime_id"] = RuntimeId,
            ["name"] = Name,
            ["automation_id"] = AutomationId,
            ["control_type"] = ControlTypeName,
            ["control_type_id"] = ControlType,
            ["bounds"] = new JsonObject
            {
                ["x"] = Left,
                ["y"] = Top,
                ["width"] = Width,
                ["height"] = Height,
            },
            ["enabled"] = Enabled,
            ["offscreen"] = Offscreen,
            ["process_id"] = ProcessId,
            ["class_name"] = ClassName,
        };

        if (Focusable)
        {
            json["focusable"] = true;
        }

        if (Focused)
        {
            json["focused"] = true;
        }

        if (!Password && !string.IsNullOrEmpty(Value))
        {
            json["value"] = Value;
        }

        if (!Password && !string.IsNullOrEmpty(Text))
        {
            json["text"] = Text;
        }

        return json;
    }
}

/// <summary>UI Automation 元素句柄；释放前可安全执行模式方法。</summary>
internal sealed class UiaElementHandle : IDisposable
{
    private readonly UiaService _owner;

    internal UiaElementHandle(UiaService owner, IUIAutomationElement element, UiaElementInfo info)
    {
        _owner = owner;
        Element = element;
        Info = info;
    }

    internal IUIAutomationElement Element { get; }

    public UiaElementInfo Info { get; }

    /// <summary>所有对 COM 元素的访问都回到 UiaService 的同步域，避免跨线程并发。</summary>
    public void Focus() => _owner.FocusElement(Element);

    public (bool Ok, string? Strategy) Invoke(UiaInvokePattern pattern = UiaInvokePattern.Auto) =>
        _owner.TryInvoke(Element, pattern);

    public string? GetText(int maxChars = 65536) => _owner.GetText(Element, maxChars);

    public string? GetValue() => _owner.GetValue(Element);

    public UiaSetValueResult SetValue(string value) => _owner.TrySetValue(Element, value);

    public void Dispose() => UiaService.ReleaseElement(Element);
}

/// <summary>裁剪后的语义树节点（仅含缓存属性，不持有 COM 对象）。</summary>
internal sealed class UiaTreeNode
{
    public UiaElementInfo Info { get; init; } = null!;

    public List<UiaTreeNode> Children { get; } = new();
}

internal sealed record UiaTreeResult(
    UiaTreeNode? Root,
    int Nodes,
    int Visited,
    int Pruned,
    bool Truncated,
    string? Reason,
    int MaxDepthReached,
    double ElapsedMs);

/// <summary>ValuePattern 写值结果；Failure 非空表示写值本身的 COM 调用失败。</summary>
internal sealed record UiaSetValueResult(
    bool Supported, bool ReadOnly, string? Value, string? Failure);

/// <summary>名称匹配方式。</summary>
internal enum UiaNameMatch
{
    Exact,
    Contains,
    Regex,
}

/// <summary>invoke_element 优先使用的 UIA 模式。</summary>
internal enum UiaInvokePattern
{
    Auto,
    Invoke,
    Toggle,
    Select,
    Expand,
    Legacy,
    Default,
}

/// <summary>UI Automation 语义通道。非线程安全，调用方需串行化。</summary>
internal sealed partial class UiaService : IDisposable
{
    private const int RuntimeIdPropertyId = 30000;
    private const int BoundingRectanglePropertyId = 30001;
    private const int ProcessIdPropertyId = 30002;
    private const int ControlTypePropertyId = 30003;
    private const int NamePropertyId = 30005;
    private const int HasKeyboardFocusPropertyId = 30008;
    private const int IsKeyboardFocusablePropertyId = 30009;
    private const int IsEnabledPropertyId = 30010;
    private const int AutomationIdPropertyId = 30011;
    private const int ClassNamePropertyId = 30012;
    private const int IsControlElementPropertyId = 30016;
    private const int IsPasswordPropertyId = 30019;
    private const int IsOffscreenPropertyId = 30022;
    private const int ValuePropertyId = 30045;

    private const int InvokePatternId = 10000;
    private const int ValuePatternId = 10002;
    private const int TextPatternId = 10014;
    private const int ExpandCollapsePatternId = 10005;
    private const int SelectionItemPatternId = 10010;
    private const int TogglePatternId = 10015;
    private const int LegacyIAccessiblePatternId = 10018;

    private static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    private static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    private const uint ClsctxInprocServer = 1;

    /// <summary>交互控件类型；interactive_only 裁剪时保留。</summary>
    private static readonly HashSet<int> InteractiveControlTypes = new()
    {
        50000, // Button
        50002, // CheckBox
        50003, // ComboBox
        50004, // Edit
        50005, // Hyperlink
        50007, // ListItem
        50011, // MenuItem
        50013, // RadioButton
        50015, // Slider
        50016, // Spinner
        50018, // Tab
        50019, // TabItem
        50024, // TreeItem
        50029, // DataItem
        50030, // Document
        50031, // SplitButton
        50035, // HeaderItem
        50040, // AppBar
    };

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    private readonly object _sync = new();
    private readonly IUIAutomation _automation;
    private int _disposed;

    // 线程本地：调用方拿到结果后立刻读，避免其它线程的下一次查询把标志覆盖。
    [ThreadStatic]
    private static bool _lastFindTruncated;

    /// <summary>最近一次 FindElements 是否因为 maxResults/5000 上限被截断。</summary>
    public bool LastFindTruncated => _lastFindTruncated;

    /// <summary>
    /// 释放 COM 引用。卡死的 provider 可能让调用阻塞，因此 ResetUia 只在后台线程调用，
    /// 并且超过保留上限的旧实例直接交给 GC。
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_trueCondition is not null)
            {
                Marshal.ReleaseComObject(_trueCondition);
            }
        }
        catch
        {
            // provider 卡死时释放可能失败；交给 GC。
        }

        try
        {
            Marshal.ReleaseComObject(_elementCache);
        }
        catch
        {
        }

        try
        {
            Marshal.FinalReleaseComObject(_automation);
        }
        catch
        {
        }
    }
    private readonly IUIAutomationCacheRequest _elementCache;
    private IUIAutomationCondition? _trueCondition;

    public UiaService()
    {
        // Native AOT 下 Interop.UIAutomationClient 的 coclass 构造函数不可用
        // （ILC 报 invalid IL），改为直接 CoCreateInstance 再取 RCW。
        Guid clsid = ClsidCUIAutomation;
        Guid iid = IidIUIAutomation;

        int hr = CoCreateInstance(
            ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out IntPtr unknown);

        if (hr != 0)
        {
            throw new COMException($"CoCreateInstance(IUIAutomation) 失败: 0x{hr:X8}", hr);
        }

        try
        {
            _automation = (IUIAutomation)Marshal.GetTypedObjectForIUnknown(unknown, typeof(IUIAutomation));
        }
        finally
        {
            Marshal.Release(unknown);
        }

        // 限制跨进程 provider 的连接/事务等待，避免单个卡死的应用拖住整个 MCP 循环。
        if (_automation is IUIAutomation2 automation2)
        {
            try
            {
                // 某些 provider（如 CCW 的 CefSharp/WinForms 混合窗口）会在
                // ElementFromPoint/Invoke 上长时间阻塞；收紧到亚秒级，配合调用方熔断。
                automation2.ConnectionTimeout = 500;
                automation2.TransactionTimeout = 1500;
            }
            catch (COMException)
            {
                // 旧系统不支持 IUIAutomation2 时保持默认值。
            }
        }

        _elementCache = CreateCacheRequest(null);
    }

    private IUIAutomationCacheRequest CreateCacheRequest(IUIAutomationCondition? treeFilter)
    {
        IUIAutomationCacheRequest cache = _automation.CreateCacheRequest();

        if (treeFilter is not null)
        {
            cache.TreeFilter = treeFilter;
        }

        // 只缓存元素自身：查询范围由 FindAll 的 TreeScope 决定，
        // 缓存范围若用 Subtree 会让每个命中元素连带缓存整棵子树。
        cache.TreeScope = TreeScope.TreeScope_Element;

        foreach (int property in new[]
                 {
                     RuntimeIdPropertyId,
                     BoundingRectanglePropertyId,
                     ProcessIdPropertyId,
                     ControlTypePropertyId,
                     NamePropertyId,
                     AutomationIdPropertyId,
                     IsEnabledPropertyId,
                     IsOffscreenPropertyId,
                     ClassNamePropertyId,
                     IsKeyboardFocusablePropertyId,
                     HasKeyboardFocusPropertyId,
                     IsPasswordPropertyId,
                     IsControlElementPropertyId,
                 })
        {
            cache.AddProperty(property);
        }

        return cache;
    }

    private IUIAutomationCondition TrueCondition =>
        _trueCondition ??= _automation.CreateTrueCondition();

    /* ------------------------------------------------------------------ */
    /* 查找                                                                */
    /* ------------------------------------------------------------------ */

}
