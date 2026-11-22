using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;

namespace VScreenPilot.Uia;

/// <summary>按指纹重定位的查询参数。</summary>
internal readonly record struct FingerprintQuery(
    IntPtr Hwnd,
    string Name,
    string AutomationId,
    string ControlTypeName,
    int Left,
    int Top,
    int Right,
    int Bottom);

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
    public UiaElementHandle? FindByFingerprint(FingerprintQuery query)
    {
        lock (_core._sync)
        {
            if (query.Name.Length == 0 &&
                query.AutomationId.Length == 0 &&
                query.ControlTypeName.Length == 0)
            {
                return null;
            }

            return FindByFingerprintCore(query);
        }
    }

    private UiaElementHandle? FindByFingerprintCore(FingerprintQuery query)
    {
        IUIAutomationCondition? condition = null;
        IUIAutomationElement? root = null;
        IUIAutomationElementArray? all = null;
        IUIAutomationElement? best = null;

        try
        {
            root = _core.FindOps.RootFromHandle(query.Hwnd);

            if (root is null)
            {
                return null;
            }

            condition = BuildFingerprintCondition(query);
            all = root.FindAllBuildCache(
                TreeScope.TreeScope_Subtree, condition, _core._elementCache);

            if (all is null)
            {
                return null;
            }

            (best, UiaElementInfo? bestInfo) = SelectBest(all, query, out int exact, out int near);

            if (best is null || bestInfo is null)
            {
                return null;
            }

            // 多个同名候选且边界附近不是唯一一个：宁可报 stale，也不猜。
            if (exact > 1 && near != 1)
            {
                UiaElementFactoryOps.ReleaseElement(best);
                best = null;
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

    private IUIAutomationCondition BuildFingerprintCondition(FingerprintQuery query) =>
        _core.Conditions.BuildCondition(
            query.Name.Length == 0 ? null : query.Name,
            query.ControlTypeName,
            query.AutomationId.Length == 0 ? null : query.AutomationId,
            null);

    internal (IUIAutomationElement? Best, UiaElementInfo? BestInfo) SelectBest(
        IUIAutomationElementArray all, FingerprintQuery query, out int exact, out int near)
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

                if (!MatchesFingerprint(info, query))
                {
                    continue;
                }

                exact++;
                bool isNear = IsNear(info!, query.Left, query.Top, query.Right, query.Bottom);

                if (isNear)
                {
                    near++;
                }

                if (best is null || (isNear && !IsNear(bestInfo!, query.Left, query.Top, query.Right, query.Bottom)))
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

    private static bool MatchesFingerprint(UiaElementInfo? info, FingerprintQuery query) =>
        info is not null &&
        info.Name == query.Name &&
        info.AutomationId == query.AutomationId &&
        string.Equals(info.ControlTypeName, query.ControlTypeName, StringComparison.OrdinalIgnoreCase);

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
