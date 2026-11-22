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

    public UiaFindOps(UiaService core) => _core = core;
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
                        UiaElementInfo? info = _core.Factory.Describe(element, cached: true);

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
                            UiaElementFactoryOps.ReleaseElement(element);
                        }
                    }
                }

                foreach (IUIAutomationElement element in textCandidates)
                {
                    try
                    {
                        string? value = _core.TextOps.GetText(element, maxChars);

                        if (value is not null &&
                            value.Contains(text, StringComparison.OrdinalIgnoreCase))
                        {
                            return _core.Factory.Describe(element, cached: false);
                        }
                    }
                    catch (COMException exception)
                    {
                        // 单个 provider 读文本失败不影响其余候选。
                        LogSink.Debug($"忽略异常: {exception.Message}");
                    }
                    finally
                    {
                        UiaElementFactoryOps.ReleaseElement(element);
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
            var results = new List<UiaElementInfo>();
            bool truncated = false;
            // prefer=largest/focused 需要完整候选集；其余调用仍按较小的上限快速返回。
            maxResults = Math.Clamp(maxResults, 1, 2000);

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
                    return new UiaFindResult(results, truncated);
                }

                IUIAutomationElementArray? all = root.FindAllBuildCache(
                    TreeScope.TreeScope_Subtree, condition, _core._elementCache);

                if (all is null)
                {
                    return new UiaFindResult(results, truncated);
                }

                try
                {
                    var usable = new List<UiaElementInfo>();
                    var hidden = new List<UiaElementInfo>();
                    int count = Math.Min(all.Length, 5000);
                    truncated = all.Length > count;

                    for (int i = 0; i < count; i++)
                    {
                        IUIAutomationElement? element = all.GetElement(i);
                        if (element is null)
                        {
                            continue;
                        }

                        try
                        {
                            UiaElementInfo? info = _core.Factory.Describe(element, cached: true);
                            if (info is null || !_core.Conditions.MatchesName(info.Name, name, nameMatch))
                            {
                                continue;
                            }

                            if (_core.Factory.IsUsable(info))
                            {
                                usable.Add(info);
                                if (usable.Count >= maxResults)
                                {
                                    // 后面还有候选（或已有 hidden）说明结果被截断。
                                    truncated |= i + 1 < count || hidden.Count > 0;
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
                            UiaElementFactoryOps.ReleaseElement(element);
                        }
                    }

                    results.AddRange(usable);
                    results.AddRange(hidden);

                    if (results.Count > maxResults)
                    {
                        results = results.Take(maxResults).ToList();
                    }

                    return new UiaFindResult(results, truncated);
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

    /// <summary>按 runtime_id 在窗口内精确定位。</summary>
}
