using System.Text.Json.Nodes;
using VScreenPilot.Uia;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>UIA 元素信息的对外 JSON 表示；从 Automation 移出，保持领域层不产出响应 JSON。</summary>
internal static class ElementPresenter
{
    /// <summary>完整或 compact 元素视图；compact 时省略诊断字段并保留旧的省略规则。</summary>
    public static ElementView ToView(UiaElementInfo info, bool compact) =>
        compact ? ToCompactView(info) : ToFullView(info);

    private static ElementView ToCompactView(UiaElementInfo info) =>
        new(
            null,
            info.Name,
            info.AutomationId,
            info.ControlTypeName,
            null,
            ToBounds(info),
            info.Enabled ? null : false,
            info.Offscreen ? true : null,
            null,
            null,
            null,
            info.Focused ? true : null,
            CompactValue(info),
            null);

    private static ElementView ToFullView(UiaElementInfo info) =>
        new(
            info.RuntimeId,
            info.Name,
            info.AutomationId,
            info.ControlTypeName,
            info.ControlType,
            ToBounds(info),
            info.Enabled,
            info.Offscreen,
            info.ProcessId,
            info.ClassName,
            info.Focusable ? true : null,
            info.Focused ? true : null,
            FullValue(info),
            info.Password || string.IsNullOrEmpty(info.Text) ? null : info.Text);

    private static BoundsView ToBounds(UiaElementInfo info) =>
        new(info.Left, info.Top, info.Width, info.Height);

    private static string? CompactValue(UiaElementInfo info)
    {
        if (info.Password || string.IsNullOrEmpty(info.Value))
        {
            return null;
        }

        return info.Value.Length > 200 ? info.Value[..200] : info.Value;
    }

    private static string? FullValue(UiaElementInfo info)
    {
        if (info.Password || string.IsNullOrEmpty(info.Value))
        {
            return null;
        }

        return info.Value;
    }

    /// <summary>find_element 条目：compact 字段 + ref，保持旧字段顺序。</summary>
    public static FindElementItemView ToFindItem(UiaElementInfo info, string reference)
    {
        ElementView view = ToView(info, compact: true);

        return new FindElementItemView(
            view.Name,
            view.AutomationId,
            view.ControlType,
            view.Bounds,
            view.Enabled,
            view.Offscreen,
            view.Focused,
            view.Value,
            reference);
    }

    public static JsonObject ToCompactJson(UiaElementInfo info)
    {
        var json = new JsonObject
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
        };

        if (!info.Enabled)
        {
            json["enabled"] = false;
        }

        if (info.Offscreen)
        {
            json["offscreen"] = true;
        }

        if (info.Focused)
        {
            json["focused"] = true;
        }

        if (!info.Password && !string.IsNullOrEmpty(info.Value))
        {
            json["value"] = info.Value.Length > 200 ? info.Value[..200] : info.Value;
        }

        return json;
    }

    public static JsonObject ToJson(UiaElementInfo info)
    {
        var json = new JsonObject
        {
            ["runtime_id"] = info.RuntimeId,
            ["name"] = info.Name,
            ["automation_id"] = info.AutomationId,
            ["control_type"] = info.ControlTypeName,
            ["control_type_id"] = info.ControlType,
            ["bounds"] = new JsonObject
            {
                ["x"] = info.Left,
                ["y"] = info.Top,
                ["width"] = info.Width,
                ["height"] = info.Height,
            },
            ["enabled"] = info.Enabled,
            ["offscreen"] = info.Offscreen,
            ["process_id"] = info.ProcessId,
            ["class_name"] = info.ClassName,
        };

        if (info.Focusable)
        {
            json["focusable"] = true;
        }

        if (info.Focused)
        {
            json["focused"] = true;
        }

        if (!info.Password && !string.IsNullOrEmpty(info.Value))
        {
            json["value"] = info.Value;
        }

        if (!info.Password && !string.IsNullOrEmpty(info.Text))
        {
            json["text"] = info.Text;
        }

        return json;
    }
}
