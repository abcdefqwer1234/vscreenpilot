using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

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

}

/// <summary>UI Automation 元素句柄；释放前可安全执行模式方法。</summary>
internal sealed class UiaElementHandle : IDisposable
{
    internal readonly UiaService _owner;

    internal UiaElementHandle(UiaService owner, IUIAutomationElement element, UiaElementInfo info)
    {
        _owner = owner;
        Element = element;
        Info = info;
    }

    internal IUIAutomationElement Element { get; }

    public UiaElementInfo Info { get; private set; }

    /// <summary>所有对 COM 元素的访问都回到所属 UiaHost 的 MTA worker。</summary>
    public void Focus() => Dispatch(service =>
    {
        service.FocusElement(Element);
        return true;
    });

    public (bool Ok, string? Strategy) Invoke(UiaInvokePattern pattern = UiaInvokePattern.Auto) =>
        Dispatch(service => service.TryInvoke(Element, pattern));

    /// <summary>请求 provider 把该控件滚入可见区；不支持时返回 false。</summary>
    public bool ScrollIntoView() => Dispatch(service => service.TryScrollIntoView(Element).Ok);

    /// <summary>滚动后重新读取几何/可见状态，避免继续使用旧的 offscreen 信息。</summary>
    public bool RefreshInfo()
    {
        UiaElementInfo? info = Dispatch(service => service.DescribeElement(Element));

        if (info is null)
        {
            return false;
        }

        Info = info;
        return true;
    }

    public string? GetText(int maxChars = 65536) =>
        Dispatch(service => service.GetText(Element, maxChars));

    public string? GetValue() => Dispatch(service => service.GetValue(Element));

    public UiaFindResult FindDescendants(
        string? name, UiaNameMatch nameMatch, string? controlType, string? automationId, int maxResults) =>
        Dispatch(service => service.FindDescendants(
            Element, name, nameMatch, controlType, automationId, maxResults));

    /// <summary>读取表格/网格；优先 GridPattern，回退 DataItem 树。</summary>
    public UiaTableData ReadTable(int maxRows, int maxCells, int rowStart = 0) =>
        Dispatch(service => service.TableOps.Read(Element, maxRows, maxCells, rowStart));

    public UiaSetValueResult SetValue(string value) =>
        Dispatch(service => service.TrySetValue(Element, value));

    public void Dispose() => Dispatch(service =>
    {
        UiaService.ReleaseElement(Element);
        return true;
    });

    internal T Dispatch<T>(Func<UiaService, T> action) =>
        _owner.Host is { } host ? host.InvokeOn(_owner, action) : action(_owner);
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
internal sealed class UiaService : IDisposable
{
    internal const int RuntimeIdPropertyId = 30000;
    internal const int BoundingRectanglePropertyId = 30001;
    internal const int ProcessIdPropertyId = 30002;
    internal const int ControlTypePropertyId = 30003;
    internal const int NamePropertyId = 30005;
    internal const int HasKeyboardFocusPropertyId = 30008;
    internal const int IsKeyboardFocusablePropertyId = 30009;
    internal const int IsEnabledPropertyId = 30010;
    internal const int AutomationIdPropertyId = 30011;
    internal const int ClassNamePropertyId = 30012;
    internal const int IsControlElementPropertyId = 30016;
    internal const int IsPasswordPropertyId = 30019;
    internal const int IsOffscreenPropertyId = 30022;
    internal const int ValuePropertyId = 30045;

    internal const int InvokePatternId = 10000;
    internal const int ValuePatternId = 10002;
    internal const int TextPatternId = 10014;
    internal const int ExpandCollapsePatternId = 10005;
    internal const int SelectionItemPatternId = 10010;
    internal const int TogglePatternId = 10015;
    internal const int ScrollItemPatternId = 10017;
    internal const int LegacyIAccessiblePatternId = 10018;

    internal static readonly Guid ClsidCUIAutomation = new("FF48DBA4-60EF-4201-AA87-54103EEF594E");
    internal static readonly Guid IidIUIAutomation = new("30CBE57D-D9D0-452A-AB13-7AC5AC4825EE");
    internal const uint ClsctxInprocServer = 1;

    /// <summary>交互控件类型；interactive_only 裁剪时保留。</summary>
    internal static readonly HashSet<int> InteractiveControlTypes = new()
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
    internal static extern int CoCreateInstance(
        ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    internal readonly object _sync = new();
    internal readonly IUIAutomation _automation;

    /// <summary>拥有该实例的 UiaHost；句柄操作据此回到同一 MTA worker。</summary>
    internal UiaHost? Host { get; set; }

    internal readonly UiaConditionOps Conditions;
    internal readonly UiaControlTypeOps ControlTypeOps;
    internal readonly UiaElementFactoryOps Factory;
    internal readonly UiaFindOps FindOps;
    internal readonly UiaLocateOps LocateOps;
    internal readonly UiaPatternOps Patterns;
    internal readonly UiaTextOps TextOps;
    internal readonly UiaTreeOps TreeOps;
    internal readonly UiaTableOps TableOps;
    internal int _disposed;

    // 线程本地：调用方拿到结果后立刻读，避免其它线程的下一次查询把标志覆盖。
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
        catch (Exception exception)
        {
            // provider 卡死时释放可能失败；交给 GC。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        try
        {
            Marshal.ReleaseComObject(_elementCache);
        }
        catch (Exception exception)
        {
            // 已显式忽略：尽力而为的清理/探测失败不影响主流程。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        try
        {
            Marshal.FinalReleaseComObject(_automation);
        }
        catch (Exception exception)
        {
            // 已显式忽略：尽力而为的清理/探测失败不影响主流程。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }
    internal readonly IUIAutomationCacheRequest _elementCache;
    internal IUIAutomationCondition? _trueCondition;

    public UiaService()
    {
        Conditions = new UiaConditionOps(this);
        ControlTypeOps = new UiaControlTypeOps(this);
        Factory = new UiaElementFactoryOps(this);
        FindOps = new UiaFindOps(this);
        LocateOps = new UiaLocateOps(this);
        Patterns = new UiaPatternOps(this);
        TextOps = new UiaTextOps(this);
        TreeOps = new UiaTreeOps(this);
        TableOps = new UiaTableOps(this);
        // Native AOT 下 Interop.UIAutomationClient 的 coclass 构造函数不可用
        // （ILC 报 invalid IL），改为直接 CoCreateInstance 再取 RCW。
        Guid clsid = ClsidCUIAutomation;
        Guid iid = IidIUIAutomation;

        int hr = CoCreateInstance(
            ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out IntPtr unknown);

        if (hr != 0)
        {
#pragma warning disable CA2201 // 保留 COMException：调用方按 HRESULT 判定（ErrorCatalog 也按它映射）
            throw new COMException($"CoCreateInstance(IUIAutomation) 失败: 0x{hr:X8}", hr);
#pragma warning restore CA2201
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
            catch (COMException exception)
            {
                // 旧系统不支持 IUIAutomation2 时保持默认值。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }

        _elementCache = CreateCacheRequest(null);
    }

    internal IUIAutomationCacheRequest CreateCacheRequest(IUIAutomationCondition? treeFilter)
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

    internal IUIAutomationCondition TrueCondition =>
        _trueCondition ??= _automation.CreateTrueCondition();

    /* ------------------------------------------------------------------ */
    /* 查找                                                                */
    /* ------------------------------------------------------------------ */

    internal static bool IsKnownControlType(string value) =>
        UiaElementFactoryOps.IsKnownControlType(value);

    internal static IReadOnlyList<string> SupportedControlTypes =>
        UiaControlTypeOps.SupportedControlTypes;

    internal static void ReleaseElement(IUIAutomationElement element) =>
        UiaElementFactoryOps.ReleaseElement(element);

    public UiaElementInfo? FindElement(
        IntPtr hwnd, string? name, string? controlType, string? automationId, string? runtimeId) =>
        FindOps.FindElement(hwnd, name, controlType, automationId, runtimeId);

    public UiaElementInfo? FindFirstElement(
        IntPtr hwnd, string? name, string? controlType, string? automationId) =>
        FindOps.FindFirstElement(hwnd, name, controlType, automationId);

    public UiaElementInfo? FindTextElement(IntPtr hwnd, string text, int maxChars = 65536) =>
        FindOps.FindTextElement(hwnd, text, maxChars);

    public List<UiaElementInfo> FindElements(
        IntPtr hwnd, string? name, UiaNameMatch nameMatch,
        string? controlType, string? automationId, int maxResults) =>
        FindOps.FindElements(hwnd, name, nameMatch, controlType, automationId, maxResults);

    public UiaFindResult FindElementsDetailed(
        IntPtr hwnd, string? name, UiaNameMatch nameMatch,
        string? controlType, string? automationId, int maxResults) =>
        FindOps.FindElementsDetailed(hwnd, name, nameMatch, controlType, automationId, maxResults);

    public UiaFindResult FindDescendants(
        IUIAutomationElement root, string? name, UiaNameMatch nameMatch,
        string? controlType, string? automationId, int maxResults) =>
        FindOps.FindDescendants(root, name, nameMatch, controlType, automationId, maxResults);

    public UiaElementHandle? FindByRuntimeId(IntPtr hwnd, string runtimeId) =>
        LocateOps.FindByRuntimeId(hwnd, runtimeId);

    public UiaElementInfo? GetFocusedElement() => LocateOps.GetFocusedElement();

    public UiaElementHandle? GetFocusedElementHandle(int? expectedPid = null) =>
        TextOps.GetFocusedElementHandle(expectedPid);

    public void FocusElement(IUIAutomationElement element) => Patterns.FocusElement(element);

    public (bool Ok, string? Strategy) TryInvoke(
        IUIAutomationElement element, UiaInvokePattern pattern = UiaInvokePattern.Auto) =>
        Patterns.TryInvoke(element, pattern);

    public (bool Ok, string? Strategy) TryScrollIntoView(IUIAutomationElement element) =>
        Patterns.TryScrollIntoView(element);

    public UiaElementInfo? DescribeElement(IUIAutomationElement element) =>
        Factory.Describe(element, cached: false);

    public bool TryInvokeAtPoint(
        int x, int y, int? expectedPid, out UiaElementInfo? info,
        Func<UiaElementInfo, bool>? confirm = null) =>
        Patterns.TryInvokeAtPoint(x, y, expectedPid, out info, confirm);

    /// <summary>只读命中测试；不触发任何 pattern。</summary>
    public UiaElementInfo? DescribeAtPoint(int x, int y, int? expectedPid) =>
        Patterns.DescribeAtPoint(x, y, expectedPid);

    public UiaSetValueResult TrySetValue(IUIAutomationElement element, string value) =>
        Patterns.TrySetValue(element, value);

    public string? GetText(IUIAutomationElement element, int maxChars = 65536) =>
        TextOps.GetText(element, maxChars);

    public string? GetValue(IUIAutomationElement element) => TextOps.GetValue(element);
}
