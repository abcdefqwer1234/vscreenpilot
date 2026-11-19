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
        lock (_sync)
        {
        var results = new List<UiaElementInfo>();
        bool truncated = false;
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
                return new UiaFindResult(results, truncated);
            }

            IUIAutomationElementArray? all = root.FindAllBuildCache(
                TreeScope.TreeScope_Subtree, condition, _elementCache);

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
                        ReleaseElement(element);
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
}
