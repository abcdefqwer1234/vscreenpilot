using Interop.UIAutomationClient;

namespace VScreenPilot.Uia;

/// <summary>FindElementsDetailed 的候选收集：按上限截断并区分可见/隐藏候选。</summary>
internal sealed class UiaFindCollector
{
    private readonly UiaService _core;
    private readonly string? _name;
    private readonly UiaNameMatch _nameMatch;
    private readonly int _maxResults;
    private readonly List<UiaElementInfo> _usable = new();
    private readonly List<UiaElementInfo> _hidden = new();

    public UiaFindCollector(UiaService core, string? name, UiaNameMatch nameMatch, int maxResults)
    {
        _core = core;
        _name = name;
        _nameMatch = nameMatch;
        _maxResults = maxResults;
    }

    public UiaFindResult Collect(IUIAutomationElementArray all)
    {
        int count = Math.Min(all.Length, 5000);
        bool truncated = all.Length > count;

        for (int index = 0; index < count && !CollectAt(all, index, count, ref truncated); index++)
        {
        }

        var results = new List<UiaElementInfo>(_usable);
        results.AddRange(_hidden);

        if (results.Count > _maxResults)
        {
            results = results.Take(_maxResults).ToList();
        }

        return new UiaFindResult(results, truncated);
    }

    private bool CollectAt(IUIAutomationElementArray all, int index, int count, ref bool truncated)
    {
        IUIAutomationElement? element = all.GetElement(index);

        if (element is null)
        {
            return false;
        }

        try
        {
            UiaElementInfo? info = _core.Factory.Describe(element, cached: true);

            if (info is null || !_core.Conditions.MatchesName(info.Name, _name, _nameMatch))
            {
                return false;
            }

            return Add(info, index, count, ref truncated);
        }
        finally
        {
            UiaElementFactoryOps.ReleaseElement(element);
        }
    }

    private bool Add(UiaElementInfo info, int index, int count, ref bool truncated)
    {
        if (!_core.Factory.IsUsable(info))
        {
            if (_hidden.Count < _maxResults)
            {
                _hidden.Add(info);
            }

            return false;
        }

        _usable.Add(info);

        if (_usable.Count < _maxResults)
        {
            return false;
        }

        // 后面还有候选（或已有 hidden）说明结果被截断。
        truncated |= index + 1 < count || _hidden.Count > 0;
        return true;
    }
}
