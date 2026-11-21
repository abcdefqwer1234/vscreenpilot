using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Diagnostics;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Perception;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

internal sealed partial class SnapshotBuilder : IDisposable
{
    /// <summary>把组装完成的 JsonObject 收敛成强类型视图；子树保持原样。</summary>
    private static SnapshotView ToView(JsonObject data)
    {
        JsonObject window = (JsonObject)data["window"]!;
        JsonNode? dialogs = data["dialogs"];
        JsonNode? windows = data["windows"];
        JsonNode? foreground = data["foreground"];
        JsonNode? annotations = data["annotations"];
        JsonNode? screenshot = data["screenshot"];
        JsonNode? preview = data["screenshot_preview"];
        string? emptyReason = data["empty_reason"]?.GetValue<string>();

        return new SnapshotView(
            data["snapshot_id"]!.GetValue<string>(),
            data["root_ref"]!.GetValue<string>(),
            (JsonObject)data["tree"]!,
            data["text"]!.GetValue<string>(),
            data["nodes"]!.GetValue<int>(),
            data["visited"]!.GetValue<int>(),
            data["pruned"]!.GetValue<int>(),
            data["truncated"]!.GetValue<bool>(),
            data["interactive_only"]!.GetValue<bool>(),
            data["max_depth"]!.GetValue<int>(),
            data["max_nodes_effective"]!.GetValue<int>(),
            data["ref_capacity"]!.GetValue<int>(),
            data["elapsed_ms"]!.GetValue<double>(),
            new SnapshotWindowView(
                window["hwnd"]!.GetValue<long>(),
                window["pid"]!.GetValue<int>(),
                window["title"]!.GetValue<string>(),
                window["exe"] is JsonValue exe ? exe.GetValue<string>() : null),
            data["privacy_redactions"]?.GetValue<int>(),
            data["empty_retry"]?.GetValue<bool>(),
            emptyReason,
            data["max_nodes_clamped"]?.GetValue<bool>(),
            data["truncated_reason"]?.GetValue<string>(),
            dialogs,
            data["root_hwnd"]?.GetValue<long>(),
            data["root_is_dialog"]?.GetValue<bool>(),
            windows,
            foreground,
            annotations,
            screenshot,
            ScreenshotPath: null,
            ScreenshotUri: null,
            ScreenshotResourceUri: null,
            ScreenshotError: null,
            ScreenshotPreview: preview);
    }

    internal JsonObject ConvertTreeNode(UiaTreeNode node, IntPtr hwnd, ref string rootRef)
    {
        UiaElementInfo info = node.Info;
        string reference = _host.RegisterElement(info, hwnd);

        if (string.IsNullOrEmpty(rootRef))
        {
            rootRef = reference;
        }

        var json = new JsonObject
        {
            ["ref"] = reference,
            ["control_type"] = info.ControlTypeName,
        };

        if (!string.IsNullOrEmpty(info.Name))
        {
            json["name"] = info.Name.Length > 200 ? info.Name[..200] : info.Name;
        }

        if (!string.IsNullOrEmpty(info.AutomationId))
        {
            json["automation_id"] = info.AutomationId;
        }

        if (!info.Password && !string.IsNullOrEmpty(info.Value))
        {
            json["value"] = info.Value.Length > 200 ? info.Value[..200] : info.Value;
        }

        json["bounds"] = new JsonObject
        {
            ["x"] = info.Left,
            ["y"] = info.Top,
            ["width"] = info.Width,
            ["height"] = info.Height,
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

        if (node.Children.Count > 0)
        {
            var children = new JsonArray();
            foreach (UiaTreeNode child in node.Children)
            {
                string unused = string.Empty;
                children.Add(ConvertTreeNode(child, hwnd, ref unused));
            }

            json["children"] = children;
        }

        return json;
    }

    private static void CollectAnnotations(
        JsonObject node, DisplayInfo display, JsonArray target, int maxCount)
    {
        if (target.Count < maxCount && node["bounds"] is JsonObject bounds)
        {
            int x = bounds["x"]?.GetValue<int>() ?? 0;
            int y = bounds["y"]?.GetValue<int>() ?? 0;
            int width = bounds["width"]?.GetValue<int>() ?? 0;
            int height = bounds["height"]?.GetValue<int>() ?? 0;
            string? name = node["name"]?.GetValue<string>();
            string controlType = node["control_type"]?.GetValue<string>() ?? string.Empty;
            bool interesting = !string.IsNullOrWhiteSpace(name) ||
                               controlType is "Button" or "Edit" or "Document" or "ListItem"
                                   or "MenuItem" or "TabItem" or "ComboBox";

            bool offscreen = node["offscreen"]?.GetValue<bool>() == true;

            if (!offscreen &&
                interesting &&
                width >= 8 && height >= 8 &&
                x + width > display.X && y + height > display.Y &&
                x < display.X + display.Width && y < display.Y + display.Height)
            {
                var annotation = new JsonObject
                {
                    ["index"] = target.Count + 1,
                    ["ref"] = node["ref"]?.GetValue<string>(),
                    ["control_type"] = controlType,
                    ["bounds"] = bounds.DeepClone(),
                };

                if (!string.IsNullOrWhiteSpace(name))
                {
                    annotation["name"] = name.Length > 80 ? name[..80] : name;
                }

                target.Add(annotation);
            }
        }

        if (node["children"] is JsonArray children)
        {
            foreach (JsonNode? child in children)
            {
                if (child is JsonObject childObject)
                {
                    CollectAnnotations(childObject, display, target, maxCount);
                }
            }
        }
    }

}
