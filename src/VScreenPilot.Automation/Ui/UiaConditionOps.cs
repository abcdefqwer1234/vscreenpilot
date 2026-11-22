using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Interop.UIAutomationClient;
using VScreenPilot.Interop;
using VScreenPilot.Errors;

namespace VScreenPilot.Uia;

/// <summary>Conditions：UiaService 的拆分子集。</summary>
internal sealed class UiaConditionOps
{
    private readonly UiaService _core;

    public UiaConditionOps(UiaService core) => _core = core;
    internal IUIAutomationCondition BuildCondition(
        string? name, string? controlType, string? automationId, string? runtimeId)
    {
        IUIAutomationCondition? condition = null;

        if (!string.IsNullOrWhiteSpace(name))
        {
            condition = And(condition, _core._automation.CreatePropertyCondition(UiaService.NamePropertyId, name));
        }

        if (!string.IsNullOrWhiteSpace(automationId))
        {
            condition = And(condition, _core._automation.CreatePropertyCondition(UiaService.AutomationIdPropertyId, automationId));
        }

        if (!string.IsNullOrWhiteSpace(controlType))
        {
            if (!UiaElementFactoryOps.TryParseControlType(controlType, out int controlTypeId))
            {
                // 静默忽略会让 find_element(control_type:"Buton") 返回第一个元素，模型点错控件。
                IEnumerable<string> supported = UiaControlTypeOps.ControlTypes.Values
                    .OrderBy(value => value, StringComparer.Ordinal);

                throw new ToolException(
                    "E_INVALID_ARG",
                    $"control_type 无效: {controlType}",
                    ToolDetails.InvalidControlType("control_type", supported),
                    retryable: false,
                    hint: "使用 UIA 控件类型英文名，例如 Button、Edit、Document",
                    stage: "validate");
            }

            condition = And(condition, _core._automation.CreatePropertyCondition(UiaService.ControlTypePropertyId, controlTypeId));
        }

        if (!string.IsNullOrWhiteSpace(runtimeId) && _core.Factory.TryParseRuntimeId(runtimeId, out int[] ids))
        {
            condition = And(condition, _core._automation.CreatePropertyCondition(UiaService.RuntimeIdPropertyId, ids));
        }

        return condition ?? _core.TrueCondition;
    }

    internal IUIAutomationCondition And(IUIAutomationCondition? existing, IUIAutomationCondition addition)
    {
        if (existing is null)
        {
            return addition;
        }

        IUIAutomationCondition combined = _core._automation.CreateAndCondition(existing, addition);

        // 组合条件已持有子条件引用；释放本地引用，避免每次查询泄漏 RCW。
        UiaElementFactoryOps.ReleaseElement(existing);
        UiaElementFactoryOps.ReleaseElement(addition);
        return combined;
    }

    internal bool MatchesName(string value, string? expected, UiaNameMatch mode)
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
}
