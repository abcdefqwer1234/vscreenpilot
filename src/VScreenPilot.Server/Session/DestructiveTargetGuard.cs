using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Errors;
using VScreenPilot.Uia;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>
/// 点击目标护栏：在注入前判断命中的控件是否属于关闭/删除/清空等破坏性动作。
/// 只做判定，不执行动作；默认策略 standard，可用 VSP_DESTRUCTIVE_GUARD=off 关闭。
/// </summary>
internal static class DestructiveTargetGuard
{
    internal enum Policy
    {
        Off,
        Standard,
        Strict,
    }

    private static readonly Regex StrongWords = new(
        @"(?i)(\b(close|delete|remove|discard|clear|uninstall|overwrite|erase|reset)\b|关闭|删除|移除|清空|放弃|卸载|覆盖|重置|\u2715|\u00d7|\u2573)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    private static readonly Regex SaveWords = new(
        @"(?i)(\b(save|apply|confirm)\b|保存|应用|确定)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>常见 AutomationId 形式：btnClose、CloseButton、mnuDelete、delete_icon 等。</summary>
    private static readonly Regex IdWords = new(
        @"(?i)(?:^|[_\W])(?:btn|button|cmd|mnu|menu|mi|ts|tab|tool)?(close|delete|remove|discard|clear|uninstall|overwrite|erase|reset)(?:btn|button|icon|item)?(?=$|[_\W])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(50));

    /// <summary>构造命中元素视图；不抛异常。</summary>
    public static HitElementView Describe(UiaElementInfo info) => new(
        info.Name,
        info.AutomationId,
        info.ControlTypeName,
        new BoundsView(info.Left, info.Top, info.Width, info.Height),
        info.Enabled,
        IsDestructive(
            info.Name, info.AutomationId, info.ControlTypeName,
            info.Left, info.Top, info.Right, info.Bottom,
            info.Left + Math.Max(1, info.Width) / 2,
            info.Top + Math.Max(1, info.Height) / 2,
            ResolvePolicy(), out _, out _));

    /// <summary>
    /// 判定并返回命中视图；破坏性目标且未允许时抛 E_DESTRUCTIVE_TARGET。
    /// 命中信息为 null（UIA 不可用）时不拦截，由调用方以 hit_source=window 报告。
    /// </summary>
    public static HitElementView? Check(
        UiaElementInfo? info, int x, int y, bool allowDestructive, string operation) =>
        Check(info, x, y, allowDestructive, operation, ResolvePolicy());

    /// <summary>指定策略版本；测试和调用方已解析策略时避免重复读环境变量。</summary>
    internal static HitElementView? Check(
        UiaElementInfo? info, int x, int y, bool allowDestructive, string operation, Policy policy)
    {
        if (info is null)
        {
            return null;
        }

        bool destructive = IsDestructive(
            info.Name, info.AutomationId, info.ControlTypeName,
            info.Left, info.Top, info.Right, info.Bottom,
            x, y, policy, out string reason, out string matched);

        var view = new HitElementView(
            info.Name,
            info.AutomationId,
            info.ControlTypeName,
            new BoundsView(info.Left, info.Top, info.Width, info.Height),
            info.Enabled,
            destructive);

        if (!destructive || allowDestructive || policy == Policy.Off)
        {
            return view;
        }

        throw new ToolException(
            "E_DESTRUCTIVE_TARGET",
            $"{operation} 命中了破坏性控件「{info.Name}」，已拒绝执行",
            new JsonObject
            {
                ["hit_element"] = new JsonObject
                {
                    ["name"] = info.Name,
                    ["automation_id"] = info.AutomationId,
                    ["control_type"] = info.ControlTypeName,
                    ["bounds"] = new JsonObject
                    {
                        ["x"] = info.Left,
                        ["y"] = info.Top,
                        ["width"] = info.Width,
                        ["height"] = info.Height,
                    },
                    ["enabled"] = info.Enabled,
                    ["destructive"] = true,
                },
                ["reason"] = reason,
                ["matched"] = matched,
                ["policy"] = policy.ToString().ToLowerInvariant(),
                ["operation"] = operation,
            },
            retryable: false,
            hint: "确认要执行破坏性操作时传 allow_destructive=true；标签页场景可改用 select_tab/close_tab",
            stage: "security");
    }

    /// <summary>供 click_ref / MSAA 使用的名称级判定。</summary>
    public static bool IsDestructive(
        string name, string automationId, string controlType,
        int left, int top, int right, int bottom, int x, int y,
        out string reason, out string matched)
    {
        return IsDestructive(
            name, automationId, controlType, left, top, right, bottom, x, y,
            ResolvePolicy(), out reason, out matched);
    }

    private static bool IsDestructive(
        string name, string automationId, string controlType,
        int left, int top, int right, int bottom, int x, int y,
        Policy policy, out string reason, out string matched)
    {
        reason = string.Empty;
        matched = string.Empty;

        // policy=off 仍然返回分类结果，只是 Check 不拦截；便于回滚时保留可观测信号。

        // 只有可交互控件才按名称判定；Document/Text 里的“删除”字样不应拦截。
        if (!IsInteractive(controlType))
        {
            return false;
        }

        string text = string.Join('\n', name, automationId);

        if (StrongWords.IsMatch(text))
        {
            reason = "name";
            matched = FirstMatch(StrongWords, text);
            return true;
        }

        if (IdWords.IsMatch(automationId))
        {
            reason = "automation_id";
            matched = FirstMatch(IdWords, automationId);
            return true;
        }

        // 标签页右缘常见自绘关闭热区：命中 TabItem 时按几何位置判定。
        if (string.Equals(controlType, "TabItem", StringComparison.OrdinalIgnoreCase))
        {
            int width = Math.Max(0, right - left);
            int zone = Math.Min(24, Math.Max(8, (int)Math.Round(width * 0.15)));

            if (width > 0 && x >= right - zone && x <= right)
            {
                reason = "tab_close_hotzone";
                matched = $"right {zone}px";
                return true;
            }
        }

        if (policy == Policy.Strict && SaveWords.IsMatch(text))
        {
            reason = "save";
            matched = FirstMatch(SaveWords, text);
            return true;
        }

        return false;
    }

    private static bool IsInteractive(string controlType) =>
        controlType is "Button" or "MenuItem" or "Hyperlink" or "SplitButton"
            or "TabItem" or "ListItem" or "TreeItem" or "CheckBox" or "RadioButton";

    private static string FirstMatch(Regex regex, string text)
    {
        Match match = regex.Match(text);
        return match.Success ? match.Value : string.Empty;
    }

    private static Policy ResolvePolicy()
    {
        string raw = (Environment.GetEnvironmentVariable("VSP_DESTRUCTIVE_GUARD") ?? "standard")
            .Trim()
            .ToLowerInvariant();

        return raw switch
        {
            "off" or "0" or "false" or "no" => Policy.Off,
            "strict" => Policy.Strict,
            _ => Policy.Standard,
        };
    }
}
