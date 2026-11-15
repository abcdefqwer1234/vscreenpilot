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
internal sealed class UiaService : IDisposable
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

    public UiaElementInfo? FindElement(
        IntPtr hwnd, string? name, string? controlType, string? automationId, string? runtimeId)
    {
        if (!string.IsNullOrWhiteSpace(runtimeId))
        {
            using UiaElementHandle? handle = FindByRuntimeId(hwnd, runtimeId);
            return handle?.Info;
        }

        // 快路径：单次 FindFirstBuildCache，命中可见元素即可返回。
        UiaElementInfo? first = FindFirstElement(hwnd, name, controlType, automationId);
        if (IsUsable(first))
        {
            return first;
        }

        // 首个命中不可见时回退到批量查询，允许后面存在可见的同名元素。
        List<UiaElementInfo> matches = FindElements(
            hwnd, name, UiaNameMatch.Exact, controlType, automationId, maxResults: 8);

        return matches.FirstOrDefault(IsUsable) ?? matches.FirstOrDefault() ?? first;
    }

    /// <summary>按精确属性查首个命中（单次 UIA 查询，供 wait_for 等轮询使用）。</summary>
    public UiaElementInfo? FindFirstElement(
        IntPtr hwnd, string? name, string? controlType, string? automationId)
    {
        lock (_sync)
        {
        IUIAutomationCondition? condition = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? element = null;

        try
        {
            root = RootFromHandle(hwnd);

            if (root is null)
            {
                return null;
            }

            condition = BuildCondition(name, controlType, automationId, null);
            element = root.FindFirstBuildCache(
                TreeScope.TreeScope_Subtree, condition, _elementCache);

            return element is null ? null : Describe(element, cached: true);
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            ReleaseElement(element);
            ReleaseElement(root);

            if (!ReferenceEquals(condition, _trueCondition))
            {
                ReleaseElement(condition);
            }
        }
        }
    }

    /// <summary>
    /// UWP 顶层是 ApplicationFrameHost，UIA 树根在子窗口 Windows.UI.Core.CoreWindow 上。
    /// 不先下钻会得到“只有边框、没有控件”的树（REG-14）。
    /// </summary>
    private static IntPtr ResolveUwpContentHwnd(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return hwnd;
        }

        var className = new StringBuilder(256);
        _ = NativeMethods.GetClassNameW(hwnd, className, className.Capacity);

        if (className.ToString().IndexOf("ApplicationFrame", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return hwnd;
        }

        IntPtr content = NativeMethods.FindWindowExW(
            hwnd, IntPtr.Zero, "Windows.UI.Core.CoreWindow", null);

        return content != IntPtr.Zero ? content : hwnd;
    }

    private IUIAutomationElement? RootFromHandle(IntPtr hwnd) =>
        hwnd == IntPtr.Zero
            ? _automation.GetRootElement()
            : _automation.ElementFromHandle(ResolveUwpContentHwnd(hwnd));

    /// <summary>
    /// 按可见文本查找元素：先匹配 Name，再对 Document/Edit/Text 读取 TextPattern。
    /// 供 wait_for{type:"text"} 使用；不做 OCR（OCR 走 read_screen_text）。
    /// </summary>
    public UiaElementInfo? FindTextElement(IntPtr hwnd, string text, int maxChars = 65536)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        lock (_sync)
        {
            IUIAutomationElement? root = null;
            IUIAutomationElementArray? all = null;

            try
            {
                root = RootFromHandle(hwnd);

                if (root is null)
                {
                    return null;
                }

                all = root.FindAllBuildCache(
                    TreeScope.TreeScope_Subtree, _trueCondition, _elementCache);

                if (all is null)
                {
                    return null;
                }

                var textCandidates = new List<IUIAutomationElement>();
                int count = Math.Min(all.Length, 3000);

                for (int index = 0; index < count; index++)
                {
                    IUIAutomationElement? element = all.GetElement(index);

                    if (element is null)
                    {
                        continue;
                    }

                    bool keepForText = false;

                    try
                    {
                        UiaElementInfo? info = Describe(element, cached: true);

                        if (info is null || info.Offscreen)
                        {
                            continue;
                        }

                        if (info.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                        {
                            return info;
                        }

                        if (info.ControlType is 50030 or 50004 or 50020)
                        {
                            // 这些类型的正文可能不在 Name 里，循环结束后统一读一次。
                            keepForText = true;
                            textCandidates.Add(element);
                        }
                    }
                    finally
                    {
                        if (!keepForText)
                        {
                            ReleaseElement(element);
                        }
                    }
                }

                foreach (IUIAutomationElement element in textCandidates)
                {
                    try
                    {
                        string? value = GetText(element, maxChars);

                        if (value is not null &&
                            value.Contains(text, StringComparison.OrdinalIgnoreCase))
                        {
                            return Describe(element, cached: false);
                        }
                    }
                    catch (COMException)
                    {
                        // 单个 provider 读文本失败不影响其余候选。
                    }
                    finally
                    {
                        ReleaseElement(element);
                    }
                }

                return null;
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                ReleaseElement(all);
                ReleaseElement(root);
            }
        }
    }

    /// <summary>按属性查询控件；name 支持 exact/contains/regex 过滤。</summary>
    public List<UiaElementInfo> FindElements(
        IntPtr hwnd,
        string? name,
        UiaNameMatch nameMatch,
        string? controlType,
        string? automationId,
        int maxResults)
    {
        lock (_sync)
        {
        var results = new List<UiaElementInfo>();
        _lastFindTruncated = false;
        // prefer=largest/focused 需要完整候选集；其余调用仍按较小的上限快速返回。
        maxResults = Math.Clamp(maxResults, 1, 2000);

        IUIAutomationCondition condition = BuildCondition(
            nameMatch == UiaNameMatch.Exact ? name : null,
            controlType,
            automationId,
            null);
        bool ownedCondition = !ReferenceEquals(condition, _trueCondition);

        IUIAutomationElement? root = null;

        try
        {
            root = RootFromHandle(hwnd);

            if (root is null)
            {
                return results;
            }

            IUIAutomationElementArray? all = root.FindAllBuildCache(
                TreeScope.TreeScope_Subtree, condition, _elementCache);

            if (all is null)
            {
                return results;
            }

            try
            {
                var usable = new List<UiaElementInfo>();
                var hidden = new List<UiaElementInfo>();
                int count = Math.Min(all.Length, 5000);
                _lastFindTruncated = all.Length > count;

                for (int i = 0; i < count; i++)
                {
                    IUIAutomationElement? element = all.GetElement(i);
                    if (element is null)
                    {
                        continue;
                    }

                    try
                    {
                        UiaElementInfo? info = Describe(element, cached: true);
                        if (info is null || !MatchesName(info.Name, name, nameMatch))
                        {
                            continue;
                        }

                        if (IsUsable(info))
                        {
                            usable.Add(info);
                            if (usable.Count >= maxResults)
                            {
                                // 后面还有候选（或已有 hidden）说明结果被截断。
                                _lastFindTruncated |= i + 1 < count || hidden.Count > 0;
                                break;
                            }
                        }
                        else if (hidden.Count < maxResults)
                        {
                            hidden.Add(info);
                        }
                    }
                    finally
                    {
                        ReleaseElement(element);
                    }
                }

                results.AddRange(usable);
                results.AddRange(hidden);

                if (results.Count > maxResults)
                {
                    results = results.Take(maxResults).ToList();
                }

                return results;
            }
            finally
            {
                ReleaseElement(all);
            }
        }
        finally
        {
            ReleaseElement(root);

            if (ownedCondition)
            {
                ReleaseElement(condition);
            }
        }
        }
    }

    /// <summary>按 runtime_id 在窗口内精确定位。</summary>
    public UiaElementHandle? FindByRuntimeId(IntPtr hwnd, string runtimeId)
    {
        lock (_sync)
        {
        if (string.IsNullOrWhiteSpace(runtimeId) ||
            !TryParseRuntimeId(runtimeId, out int[] ids))
        {
            return null;
        }

        IUIAutomationCondition? condition = null;
        IUIAutomationElement? root = null;
        IUIAutomationElement? element = null;

        try
        {
            root = RootFromHandle(hwnd);

            if (root is null)
            {
                return null;
            }

            condition = _automation.CreatePropertyCondition(RuntimeIdPropertyId, ids);
            element = root.FindFirstBuildCache(
                TreeScope.TreeScope_Subtree, condition, _elementCache);

            if (element is null)
            {
                return null;
            }

            UiaElementInfo? info = Describe(element, cached: true);
            if (info is null)
            {
                return null;
            }

            var handle = new UiaElementHandle(this, element, info);
            element = null;
            return handle;
        }
        finally
        {
            ReleaseElement(element);
            ReleaseElement(root);
            ReleaseElement(condition);
        }
        }
    }

    /// <summary>
    /// 按指纹（name/automation_id/control_type + 旧边界）重定位控件。
    /// 只有唯一匹配（或边界附近唯一匹配）时才返回，避免误命中相邻列表项。
    /// </summary>
    public UiaElementHandle? FindByFingerprint(
        IntPtr hwnd,
        string name,
        string automationId,
        string controlTypeName,
        int left,
        int top,
        int right,
        int bottom)
    {
        lock (_sync)
        {
        if (string.IsNullOrEmpty(name) &&
            string.IsNullOrEmpty(automationId) &&
            string.IsNullOrEmpty(controlTypeName))
        {
            return null;
        }

        IUIAutomationCondition? condition = null;
        IUIAutomationElement? root = null;
        IUIAutomationElementArray? all = null;
        IUIAutomationElement? best = null;
        UiaElementInfo? bestInfo = null;

        try
        {
            root = RootFromHandle(hwnd);

            if (root is null)
            {
                return null;
            }

            condition = BuildCondition(
                string.IsNullOrEmpty(name) ? null : name,
                controlTypeName,
                string.IsNullOrEmpty(automationId) ? null : automationId,
                null);

            all = root.FindAllBuildCache(
                TreeScope.TreeScope_Subtree, condition, _elementCache);

            if (all is null)
            {
                return null;
            }

            int count = Math.Min(all.Length, 200);
            int exact = 0;
            int near = 0;

            for (int i = 0; i < count; i++)
            {
                IUIAutomationElement? element = all.GetElement(i);
                if (element is null)
                {
                    continue;
                }

                bool keep = false;

                try
                {
                    UiaElementInfo? info = Describe(element, cached: true);
                    if (info is null ||
                        info.Name != name ||
                        info.AutomationId != automationId ||
                        !string.Equals(info.ControlTypeName, controlTypeName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    exact++;
                    bool isNear = IsNear(info, left, top, right, bottom);
                    if (isNear)
                    {
                        near++;
                    }

                    if (best is null || (isNear && !IsNear(bestInfo!, left, top, right, bottom)))
                    {
                        ReleaseElement(best);
                        best = element;
                        bestInfo = info;
                        keep = true;
                    }
                }
                finally
                {
                    if (!keep)
                    {
                        ReleaseElement(element);
                    }
                }
            }

            if (best is null || bestInfo is null)
            {
                return null;
            }

            // 多个同名候选且边界附近不是唯一一个：宁可报 stale，也不猜。
            if (exact > 1 && near != 1)
            {
                ReleaseElement(best);
                return null;
            }

            var handle = new UiaElementHandle(this, best, bestInfo);
            best = null;
            return handle;
        }
        finally
        {
            ReleaseElement(best);
            ReleaseElement(all);
            ReleaseElement(root);

            if (!ReferenceEquals(condition, _trueCondition))
            {
                ReleaseElement(condition);
            }
        }
        }
    }

    private static bool IsNear(UiaElementInfo info, int left, int top, int right, int bottom)
    {
        const int slack = 24;
        return info.Left < right + slack &&
               info.Right > left - slack &&
               info.Top < bottom + slack &&
               info.Bottom > top - slack;
    }

    public UiaElementInfo? GetFocusedElement()
    {
        lock (_sync)
        {
            try
            {
                IUIAutomationElement? element = _automation.GetFocusedElementBuildCache(_elementCache);
                if (element is null)
                {
                    return null;
                }

                try
                {
                    UiaElementInfo? info = Describe(element, cached: true);
                    if (info is null ||
                        (info.Width == 0 && info.Height == 0 && info.Name.Length == 0))
                    {
                        info = DescribeCurrent(element) ?? info;
                    }

                    return info;
                }
                finally
                {
                    ReleaseElement(element);
                }
            }
            catch (COMException)
            {
                return null;
            }
        }
    }

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

    /// <summary>读取 ValuePattern 的当前值；不支持时返回 null。</summary>
    public string? GetValue(IUIAutomationElement element)
    {
        lock (_sync)
        {
        object? pattern = null;

        try
        {
            pattern = element.GetCurrentPattern(ValuePatternId);
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
            ReleaseElement(pattern);
        }
        }
    }

    /// <summary>
    /// 读取文本控件正文：优先 TextPattern，其次 ValuePattern。
    /// Word 正文（Edit/Body）通常只有 TextPattern，没有 ValuePattern。
    /// </summary>
    public string? GetText(IUIAutomationElement element, int maxChars = 65536)
    {
        lock (_sync)
        {
        object? pattern = null;
        object? range = null;

        try
        {
            pattern = element.GetCurrentPattern(TextPatternId);

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
        }
        finally
        {
            ReleaseElement(range);
            ReleaseElement(pattern);
        }

        return GetValue(element);
        }
    }

    /// <summary>返回当前焦点元素句柄；调用方负责 Dispose。expectedPid 用于拒绝焦点落在其他进程。</summary>
    public UiaElementHandle? GetFocusedElementHandle(int? expectedPid = null)
    {
        IUIAutomationElement? element = null;

        try
        {
            element = _automation.GetFocusedElementBuildCache(_elementCache);

            if (element is null)
            {
                return null;
            }

            UiaElementInfo? info = Describe(element, cached: true);

            if (info is null || (info.Width == 0 && info.Height == 0 && info.Name.Length == 0))
            {
                info = DescribeCurrent(element) ?? info;
            }

            if (info is null)
            {
                return null;
            }

            if (expectedPid.HasValue && info.ProcessId != expectedPid.Value)
            {
                return null;
            }

            var handle = new UiaElementHandle(this, element, info);
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
            ReleaseElement(element);
        }
    }

    private static string NormalizeText(string? text) =>
        string.IsNullOrEmpty(text)
            ? string.Empty
            : text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static string ReadText(IUIAutomationElement element, int controlType)
    {
        if (controlType is not (50004 or 50030))
        {
            return string.Empty;
        }

        object? pattern = null;
        object? range = null;

        try
        {
            pattern = element.GetCurrentPattern(TextPatternId);

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
            ReleaseElement(range);
            ReleaseElement(pattern);
        }

        return string.Empty;
    }

    private static string ReadTextCurrent(IUIAutomationElement element)
    {
        object? pattern = null;
        object? range = null;

        try
        {
            pattern = element.GetCurrentPattern(TextPatternId);

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
            ReleaseElement(range);
            ReleaseElement(pattern);
        }

        return string.Empty;
    }

    private static string? SafeString(string? value) => value;

    /* ------------------------------------------------------------------ */
    /* 语义树                                                              */
    /* ------------------------------------------------------------------ */

    public UiaTreeResult BuildTree(
        IntPtr hwnd,
        string? rootRuntimeId,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        int timeBudgetMs,
        Func<bool>? isCancelled)
    {
        // 树构建同样访问 IUIAutomation；必须与 find/invoke 串行，否则会跨线程使用 COM 对象。
        lock (_sync)
        {
            return BuildTreeCore(
                hwnd, rootRuntimeId, maxDepth, interactiveOnly, maxNodes, timeBudgetMs, isCancelled);
        }
    }

    private UiaTreeResult BuildTreeCore(
        IntPtr hwnd,
        string? rootRuntimeId,
        int maxDepth,
        bool interactiveOnly,
        int maxNodes,
        int timeBudgetMs,
        Func<bool>? isCancelled)
    {
        IUIAutomationElement? rootElement = null;
        UiaElementHandle? rootHandle = null;
        var stopwatch = Stopwatch.StartNew();

        maxDepth = Math.Clamp(maxDepth, 1, 10);
        maxNodes = Math.Clamp(maxNodes, 1, 2000);

        try
        {
            if (!string.IsNullOrWhiteSpace(rootRuntimeId))
            {
                rootHandle = FindByRuntimeId(hwnd, rootRuntimeId);
                rootElement = rootHandle?.Element;

                if (rootElement is null)
                {
                    return new UiaTreeResult(null, 0, 0, 0, false, "root_not_found", 0, 0);
                }

                // 句柄负责释放；这里用同一对象继续查询。
            }
            else
            {
                // 必须使用 BuildCache 版本，否则后续读取 Cached* 属性会抛 ArgumentException。
                rootElement = hwnd != IntPtr.Zero
                    ? _automation.ElementFromHandleBuildCache(
                        ResolveUwpContentHwnd(hwnd), _elementCache)
                    : _automation.GetRootElementBuildCache(_elementCache);
            }

            if (rootElement is null)
            {
                return new UiaTreeResult(null, 0, 0, 0, false, "root_not_found", 0, 0);
            }

            UiaElementInfo? rootInfo = Describe(rootElement, cached: true);
            if (rootInfo is null)
            {
                return new UiaTreeResult(null, 0, 0, 0, false, "root_unavailable", 0, 0);
            }

            var state = new TreeBuildState
            {
                MaxDepth = maxDepth,
                MaxNodes = maxNodes,
                InteractiveOnly = interactiveOnly,
                Deadline = timeBudgetMs,
                Stopwatch = stopwatch,
                VisitedLimit = Math.Max(64, maxNodes * 6),
                IsCancelled = isCancelled,
                Nodes = 1,
            };

            var root = new UiaTreeNode { Info = rootInfo };
            BuildChildren(rootElement, root, 1, state);

            return new UiaTreeResult(
                root,
                state.Nodes,
                state.Visited,
                state.Pruned,
                state.Truncated,
                state.Reason,
                state.MaxDepthReached,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
        }
        catch (COMException)
        {
            return new UiaTreeResult(
                null, 0, 0, 0, false, "uia_error", 0,
                Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2));
        }
        finally
        {
            rootHandle?.Dispose();
            if (rootHandle is null)
            {
                ReleaseElement(rootElement);
            }
        }
    }

    private void BuildChildren(
        IUIAutomationElement element, UiaTreeNode node, int depth, TreeBuildState state)
    {
        if (depth > state.MaxDepth)
        {
            return;
        }

        if (state.IsCancelled?.Invoke() == true)
        {
            state.Truncate("cancelled");
            return;
        }

        if (state.Stopwatch.ElapsedMilliseconds > state.Deadline)
        {
            state.Truncate("time_budget");
            return;
        }

        IUIAutomationElementArray? children = null;

        try
        {
            children = element.FindAllBuildCache(
                TreeScope.TreeScope_Children, TrueCondition, _elementCache);
        }
        catch (COMException)
        {
            return;
        }

        if (children is null)
        {
            return;
        }

        try
        {
            int count = children.Length;

            for (int i = 0; i < count; i++)
            {
                if (state.Nodes >= state.MaxNodes)
                {
                    state.Truncate("max_nodes");
                    return;
                }

                if (state.Visited >= state.VisitedLimit)
                {
                    state.Truncate("visited_limit");
                    return;
                }

                if ((i & 7) == 0)
                {
                    if (state.Stopwatch.ElapsedMilliseconds > state.Deadline)
                    {
                        state.Truncate("time_budget");
                        return;
                    }

                    if (state.IsCancelled?.Invoke() == true)
                    {
                        state.Truncate("cancelled");
                        return;
                    }
                }

                IUIAutomationElement? child = null;

                try
                {
                    child = children.GetElement(i);
                    if (child is null)
                    {
                        continue;
                    }

                    state.Visited++;
                    state.MaxDepthReached = Math.Max(state.MaxDepthReached, depth);

                    UiaElementInfo? info = Describe(child, cached: true);
                    if (info is null)
                    {
                        continue;
                    }

                    if (IsNoiseControl(info.ControlType))
                    {
                        // 滚动条/进度条/分隔条内部全是机械按钮，进树只会污染上下文。
                        continue;
                    }

                    var childNode = new UiaTreeNode { Info = info };
                    BuildChildren(child, childNode, depth + 1, state);

                    if (state.InteractiveOnly && !IsInteresting(childNode))
                    {
                        state.Pruned++;

                        // 单子节点的无意义容器（Chromium 常见）直接上提，别让它吃掉深度预算。
                        if (childNode.Children.Count == 1)
                        {
                            node.Children.Add(childNode.Children[0]);
                        }

                        continue;
                    }

                    // 递归可能已经用满预算；此时不能再把当前节点计入，否则 max_nodes 会被超过。
                    if (state.Nodes >= state.MaxNodes)
                    {
                        state.Truncate("max_nodes");
                        return;
                    }

                    state.Nodes++;
                    node.Children.Add(childNode);
                }
                finally
                {
                    ReleaseElement(child);
                }
            }
        }
        finally
        {
            ReleaseElement(children);
        }
    }

    private static bool IsInteresting(UiaTreeNode node) =>
        node.Children.Count > 0 ||
        node.Info.Focusable ||
        node.Info.Focused ||
        InteractiveControlTypes.Contains(node.Info.ControlType) ||
        // 画布/自绘应用常把内容暴露成无子节点、不可聚焦的 Text/Image/Custom。
        // 只要它带名字，就保留，否则 interactive_only 会把内容整片剪掉（S4）。
        (node.Info.ControlType is 50020 or 50006 or 50025 &&
         !string.IsNullOrWhiteSpace(node.Info.Name));

    private static bool IsNoiseControl(int controlType) =>
        controlType is 50012 or 50014 or 50027 or 50038; // ProgressBar/ScrollBar/Thumb/Separator

    private sealed class TreeBuildState
    {
        public int MaxDepth;
        public int MaxNodes;
        public bool InteractiveOnly;
        public int Deadline;
        public Stopwatch Stopwatch = null!;
        public int VisitedLimit;
        public Func<bool>? IsCancelled;
        public int Nodes;
        public int Visited;
        public int Pruned;
        public int MaxDepthReached;
        public bool Truncated;
        public string? Reason;

        public void Truncate(string reason)
        {
            if (Truncated)
            {
                return;
            }

            Truncated = true;
            Reason = reason;
        }
    }

    /* ------------------------------------------------------------------ */
    /* 条件与元数据                                                        */
    /* ------------------------------------------------------------------ */

    private IUIAutomationCondition BuildCondition(
        string? name, string? controlType, string? automationId, string? runtimeId)
    {
        IUIAutomationCondition? condition = null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            condition = And(condition, _automation.CreatePropertyCondition(NamePropertyId, name));
        }

        if (!string.IsNullOrWhiteSpace(automationId))
        {
            condition = And(condition, _automation.CreatePropertyCondition(AutomationIdPropertyId, automationId));
        }

        if (!string.IsNullOrWhiteSpace(controlType))
        {
            if (!TryParseControlType(controlType, out int controlTypeId))
            {
                // 静默忽略会让 find_element(control_type:"Buton") 返回第一个元素，模型点错控件。
                var supported = new JsonArray();

                foreach (string typeName in ControlTypes.Values.OrderBy(value => value, StringComparer.Ordinal))
                {
                    supported.Add(typeName);
                }

                throw new ToolException(
                    "E_INVALID_ARG",
                    $"control_type 无效: {controlType}",
                    new JsonObject { ["field"] = "control_type", ["supported"] = supported },
                    retryable: false,
                    hint: "使用 UIA 控件类型英文名，例如 Button、Edit、Document",
                    stage: "validate");
            }

            condition = And(condition, _automation.CreatePropertyCondition(ControlTypePropertyId, controlTypeId));
        }

        if (!string.IsNullOrWhiteSpace(runtimeId) && TryParseRuntimeId(runtimeId, out int[] ids))
        {
            condition = And(condition, _automation.CreatePropertyCondition(RuntimeIdPropertyId, ids));
        }

        return condition ?? TrueCondition;
    }

    private IUIAutomationCondition And(IUIAutomationCondition? existing, IUIAutomationCondition addition)
    {
        if (existing is null)
        {
            return addition;
        }

        IUIAutomationCondition combined = _automation.CreateAndCondition(existing, addition);

        // 组合条件已持有子条件引用；释放本地引用，避免每次查询泄漏 RCW。
        ReleaseElement(existing);
        ReleaseElement(addition);
        return combined;
    }

    private static bool MatchesName(string value, string? expected, UiaNameMatch mode)
    {
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        return mode switch
        {
            UiaNameMatch.Contains => value.Contains(expected, StringComparison.OrdinalIgnoreCase),
            UiaNameMatch.Regex => System.Text.RegularExpressions.Regex.IsMatch(
                value, expected,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100)),
            _ => string.Equals(value, expected, StringComparison.Ordinal),
        };
    }

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
        catch
        {
            // 释放失败不影响结果，交给 GC 兜底。
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

    private static string ControlTypeName(int controlType) =>
        ControlTypes.TryGetValue(controlType, out string? name) ? name : $"Custom({controlType})";

    private static readonly Dictionary<int, string> ControlTypes = new()
    {
        [50000] = "Button",
        [50001] = "Calendar",
        [50002] = "CheckBox",
        [50003] = "ComboBox",
        [50004] = "Edit",
        [50005] = "Hyperlink",
        [50006] = "Image",
        [50007] = "ListItem",
        [50008] = "List",
        [50009] = "Menu",
        [50010] = "MenuBar",
        [50011] = "MenuItem",
        [50012] = "ProgressBar",
        [50013] = "RadioButton",
        [50014] = "ScrollBar",
        [50015] = "Slider",
        [50016] = "Spinner",
        [50017] = "StatusBar",
        [50018] = "Tab",
        [50019] = "TabItem",
        [50020] = "Text",
        [50021] = "ToolBar",
        [50022] = "ToolTip",
        [50023] = "Tree",
        [50024] = "TreeItem",
        [50025] = "Custom",
        [50026] = "Group",
        [50027] = "Thumb",
        [50028] = "DataGrid",
        [50029] = "DataItem",
        [50030] = "Document",
        [50031] = "SplitButton",
        [50032] = "Window",
        [50033] = "Pane",
        [50034] = "Header",
        [50035] = "HeaderItem",
        [50036] = "Table",
        [50037] = "TitleBar",
        [50038] = "Separator",
        [50039] = "SemanticZoom",
        [50040] = "AppBar",
    };
}
