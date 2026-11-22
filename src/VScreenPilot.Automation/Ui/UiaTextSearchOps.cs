using System.Runtime.InteropServices;
using Interop.UIAutomationClient;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Uia;

/// <summary>FindTextElement 的文本扫描：先匹配 Name，再对正文控件读 TextPattern。</summary>
internal sealed class UiaTextSearchOps
{
    private readonly UiaService _core;

    public UiaTextSearchOps(UiaService core) => _core = core;

    internal UiaElementInfo? Search(IUIAutomationElementArray all, string text, int maxChars)
    {
        var candidates = new List<IUIAutomationElement>();
        UiaElementInfo? matched = ScanNames(all, text, candidates);
        return matched ?? ScanTexts(candidates, text, maxChars);
    }

    private UiaElementInfo? ScanNames(
        IUIAutomationElementArray all, string text, List<IUIAutomationElement> candidates)
    {
        int count = Math.Min(all.Length, 3000);

        for (int index = 0; index < count; index++)
        {
            IUIAutomationElement? element = all.GetElement(index);

            if (element is null)
            {
                continue;
            }

            UiaElementInfo? found = InspectNameCandidate(element, text, candidates);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    private UiaElementInfo? InspectNameCandidate(
        IUIAutomationElement element, string text, List<IUIAutomationElement> candidates)
    {
        bool keepForText = false;

        try
        {
            UiaElementInfo? info = _core.Factory.Describe(element, cached: true);

            if (info is null || info.Offscreen)
            {
                return null;
            }

            if (info.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }

            if (info.ControlType is not (50030 or 50004 or 50020))
            {
                return null;
            }

            // 这些类型的正文可能不在 Name 里，循环结束后统一读一次。
            keepForText = true;
            candidates.Add(element);
            return null;
        }
        finally
        {
            if (!keepForText)
            {
                UiaElementFactoryOps.ReleaseElement(element);
            }
        }
    }

    private UiaElementInfo? ScanTexts(
        List<IUIAutomationElement> candidates, string text, int maxChars)
    {
        foreach (IUIAutomationElement element in candidates)
        {
            try
            {
                UiaElementInfo? info = MatchText(element, text, maxChars);

                if (info is not null)
                {
                    return info;
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

    private UiaElementInfo? MatchText(IUIAutomationElement element, string text, int maxChars)
    {
        string? value = _core.TextOps.GetText(element, maxChars);

        if (value is null || !value.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _core.Factory.Describe(element, cached: false);
    }
}
