using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;

namespace VScreenPilot.Uia;

/// <summary>Locate：UiaService 的拆分子集。</summary>
internal sealed class UiaLocateOps
{
    private readonly UiaService _core;

    public UiaLocateOps(UiaService core) => _core = core;
    public UiaElementHandle? FindByRuntimeId(IntPtr hwnd, string runtimeId)
    {
        lock (_core._sync)
        {
            if (string.IsNullOrWhiteSpace(runtimeId) ||
                !_core.Factory.TryParseRuntimeId(runtimeId, out int[] ids))
            {
                return null;
            }

            IUIAutomationCondition? condition = null;
            IUIAutomationElement? root = null;
            IUIAutomationElement? element = null;

            try
            {
                root = _core.FindOps.RootFromHandle(hwnd);

                if (root is null)
                {
                    return null;
                }

                condition = _core._automation.CreatePropertyCondition(UiaService.RuntimeIdPropertyId, ids);
                element = root.FindFirstBuildCache(
                    TreeScope.TreeScope_Subtree, condition, _core._elementCache);

                if (element is null)
                {
                    return null;
                }

                UiaElementInfo? info = _core.Factory.Describe(element, cached: true);
                if (info is null)
                {
                    return null;
                }

                var handle = new UiaElementHandle(_core, element, info);
                element = null;
                return handle;
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(element);
                UiaElementFactoryOps.ReleaseElement(root);
                UiaElementFactoryOps.ReleaseElement(condition);
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
        lock (_core._sync)
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
                root = _core.FindOps.RootFromHandle(hwnd);

                if (root is null)
                {
                    return null;
                }

                condition = _core.Conditions.BuildCondition(
                    string.IsNullOrEmpty(name) ? null : name,
                    controlTypeName,
                    string.IsNullOrEmpty(automationId) ? null : automationId,
                    null);

                all = root.FindAllBuildCache(
                    TreeScope.TreeScope_Subtree, condition, _core._elementCache);

                if (all is null)
                {
                    return null;
                }

                (best, bestInfo) = SelectBest(
                    all, name, automationId, controlTypeName, left, top, right, bottom,
                    out int exact, out int near);

                if (best is null || bestInfo is null)
                {
                    return null;
                }

                // 多个同名候选且边界附近不是唯一一个：宁可报 stale，也不猜。
                if (exact > 1 && near != 1)
                {
                    UiaElementFactoryOps.ReleaseElement(best);
                    return null;
                }

                var handle = new UiaElementHandle(_core, best, bestInfo);
                best = null;
                return handle;
            }
            finally
            {
                UiaElementFactoryOps.ReleaseElement(best);
                UiaElementFactoryOps.ReleaseElement(all);
                UiaElementFactoryOps.ReleaseElement(root);

                if (!ReferenceEquals(condition, _core._trueCondition))
                {
                    UiaElementFactoryOps.ReleaseElement(condition);
                }
            }
        }
    }

    internal (IUIAutomationElement? Best, UiaElementInfo? BestInfo) SelectBest(
        IUIAutomationElementArray all, string name, string? automationId, string? controlTypeName,
        int left, int top, int right, int bottom, out int exact, out int near)
    {
        IUIAutomationElement? best = null;
        UiaElementInfo? bestInfo = null;

        int count = Math.Min(all.Length, 200);
        exact = 0;
        near = 0;

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
                UiaElementInfo? info = _core.Factory.Describe(element, cached: true);
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
                    UiaElementFactoryOps.ReleaseElement(best);
                    best = element;
                    bestInfo = info;
                    keep = true;
                }
            }
            finally
            {
                if (!keep)
                {
                    UiaElementFactoryOps.ReleaseElement(element);
                }
            }
        }

        return (best, bestInfo);
    }

    internal bool IsNear(UiaElementInfo info, int left, int top, int right, int bottom)
    {
        const int slack = 24;
        return info.Left < right + slack &&
               info.Right > left - slack &&
               info.Top < bottom + slack &&
               info.Bottom > top - slack;
    }

    public UiaElementInfo? GetFocusedElement()
    {
        lock (_core._sync)
        {
            try
            {
                IUIAutomationElement? element = _core._automation.GetFocusedElementBuildCache(_core._elementCache);
                if (element is null)
                {
                    return null;
                }

                try
                {
                    UiaElementInfo? info = _core.Factory.Describe(element, cached: true);
                    if (info is null ||
                        (info.Width == 0 && info.Height == 0 && info.Name.Length == 0))
                    {
                        info = _core.Factory.DescribeCurrent(element) ?? info;
                    }

                    return info;
                }
                finally
                {
                    UiaElementFactoryOps.ReleaseElement(element);
                }
            }
            catch (COMException)
            {
                return null;
            }
        }
    }
}
