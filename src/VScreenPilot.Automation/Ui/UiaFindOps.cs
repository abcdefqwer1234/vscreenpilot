using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Uia;

/// <summary>Find：UiaService 的拆分子集。</summary>
internal sealed class UiaFindOps
{
    private readonly UiaService _core;
    private readonly UiaTextSearchOps _textSearch;

    public UiaFindOps(UiaService core)
    {
        _core = core;
        _textSearch = new UiaTextSearchOps(core);
    }
    public UiaElementInfo? FindElement(
        IntPtr hwnd, string? name, string? controlType, string? automationId, string? runtimeId)
    {
        if (!string.IsNullOrWhiteSpace(runtimeId))
        {
            using UiaElementHandle? handle = _core.LocateOps.FindByRuntimeId(hwnd, runtimeId);
            return handle?.Info;
        }

        // 快路径：单次 FindFirstBuildCache，命中可见元素即可返回。
        UiaElementInfo? first = FindFirstElement(hwnd, name, controlType, automationId);
        if (_core.Factory.IsUsable(first))
        {
            return first;
        }

        // 首个命中不可见时回退到批量查询，允许后面存在可见的同名元素。
        List<UiaElementInfo> matches = FindElements(
            hwnd, name, UiaNameMatch.Exact, controlType, automationId, maxResults: 8);

        return matches.FirstOrDefault(_core.Factory.IsUsable) ?? matches.FirstOrDefault() ?? first;
    }

    /// <summary>按精确属性查首个命中（单次 UIA 查询，供 wait_for 等轮询使用）。</summary>
    public UiaElementInfo? FindFirstElement(
        IntPtr hwnd, string? name, string? controlType, string? automationId)
    {
        lock (_core._sync)
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

                condition = _core.Conditions.BuildCondition(name, controlType, automationId, null);
                element = root.FindFirstBuildCache(
                    TreeScope.TreeScope_Subtree, condition, _core._elementCache);

                return element is null ? null : _core.Factory.Describe(element, cached: true);
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(element);
                UiaElementFactoryOps.ReleaseElement(root);

                if (!ReferenceEquals(condition, _core._trueCondition))
                {
                    UiaElementFactoryOps.ReleaseElement(condition);
                }
            }
        }
    }

    /// <summary>
    /// UWP 顶层是 ApplicationFrameHost，UIA 树根在子窗口 Windows.UI.Core.CoreWindow 上。
    /// 不先下钻会得到“只有边框、没有控件”的树（REG-14）。
    /// </summary>
    internal IntPtr ResolveUwpContentHwnd(IntPtr hwnd)
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

    internal IUIAutomationElement? RootFromHandle(IntPtr hwnd) =>
        hwnd == IntPtr.Zero
            ? _core._automation.GetRootElement()
            : _core._automation.ElementFromHandle(ResolveUwpContentHwnd(hwnd));

    /// <summary>
    /// 按可见文本查找元素：先匹配 Name，再对 Document/Edit/Text 读取 TextPattern。
    /// 供 wait_for{type:"text"} 使用；不做 OCR（OCR 走 read_screen_text）。
    /// </summary>
    /// <summary>
    /// 以已有元素为根做子树查找。name 的 contains/regex 仍由收集器过滤；
    /// 调用方负责 root 的生命周期（通常是 UiaElementHandle.Element）。
    /// </summary>
    public UiaFindResult FindDescendants(
        IUIAutomationElement root,
        string? name,
        UiaNameMatch nameMatch,
        string? controlType,
        string? automationId,
        int maxResults)
    {
        lock (_core._sync)
        {
            int limit = Math.Clamp(maxResults, 1, 2000);
            IUIAutomationCondition condition = _core.Conditions.BuildCondition(
                nameMatch == UiaNameMatch.Exact ? name : null,
                controlType,
                automationId,
                null);
            bool ownedCondition = !ReferenceEquals(condition, _core._trueCondition);
            IUIAutomationElementArray? all = null;

            try
            {
                all = root.FindAllBuildCache(
                    TreeScope.TreeScope_Subtree, condition, _core._elementCache);

                if (all is null)
                {
                    return new UiaFindResult(new List<UiaElementInfo>(), false);
                }

                return new UiaFindCollector(_core, name, nameMatch, limit).Collect(all);
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(all);

                if (ownedCondition)
                {
                    UiaElementFactoryOps.ReleaseElement(condition);
                }
            }
        }
    }

    public UiaElementInfo? FindTextElement(IntPtr hwnd, string text, int maxChars = 65536)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        lock (_core._sync)
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
                    TreeScope.TreeScope_Subtree, _core._trueCondition, _core._elementCache);

                if (all is null)
                {
                    return null;
                }

                return _textSearch.Search(all, text, maxChars);
            }
            catch (COMException)
            {
                return null;
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(all);
                UiaElementFactoryOps.ReleaseElement(root);
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
        int maxResults) =>
        FindElementsDetailed(hwnd, name, nameMatch, controlType, automationId, maxResults).Items;

    /// <summary>
    /// 同 <see cref="FindElements"/>，但把“是否因上限/隐藏候选被截断”作为返回值携带，
    /// 不再依赖 [ThreadStatic] 之类的跨调用隐藏通道。
    /// </summary>
    public UiaFindResult FindElementsDetailed(
        IntPtr hwnd,
        string? name,
        UiaNameMatch nameMatch,
        string? controlType,
        string? automationId,
        int maxResults)
    {
        lock (_core._sync)
        {
            // prefer=largest/focused 需要完整候选集；其余调用仍按较小的上限快速返回。
            int limit = Math.Clamp(maxResults, 1, 2000);

            IUIAutomationCondition condition = _core.Conditions.BuildCondition(
                nameMatch == UiaNameMatch.Exact ? name : null,
                controlType,
                automationId,
                null);
            bool ownedCondition = !ReferenceEquals(condition, _core._trueCondition);

            IUIAutomationElement? root = null;

            try
            {
                root = RootFromHandle(hwnd);

                if (root is null)
                {
                    return new UiaFindResult(new List<UiaElementInfo>(), false);
                }

                IUIAutomationElementArray? all = root.FindAllBuildCache(
                    TreeScope.TreeScope_Subtree, condition, _core._elementCache);

                if (all is null)
                {
                    return new UiaFindResult(new List<UiaElementInfo>(), false);
                }

                try
                {
                    return new UiaFindCollector(_core, name, nameMatch, limit).Collect(all);
                }
                finally
                {
                    UiaElementFactoryOps.ReleaseElement(all);
                }
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(root);

                if (ownedCondition)
                {
                    UiaElementFactoryOps.ReleaseElement(condition);
                }
            }
        }
    }
}
