using System.Text.Json.Nodes;
using VScreenPilot.Uia;

namespace VScreenPilot.Session;

/// <summary>UIA 元素信息的对外 JSON 表示；从 Automation 移出，保持领域层不产出响应 JSON。</summary>
internal static class ElementPresenter
{
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
