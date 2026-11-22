using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Ui;
using VScreenPilot.Perception;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>ConvertOps：SnapshotBuilder 的拆分子集。</summary>
internal sealed class SnapshotConvertOps
{
    private readonly SnapshotBuilder _core;

    public SnapshotConvertOps(SnapshotBuilder core) => _core = core;
    /// <summary>把组装完成的 JsonObject 收敛成强类型视图；子树保持原样。</summary>
    internal SnapshotView ToView(JsonObject data)
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
        string reference = _core._state.RegisterElement(info, hwnd);

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

    internal void CollectAnnotations(
        JsonObject node, DisplayInfo display, JsonArray target, int maxCount)
    {
        if (target.Count < maxCount && node["bounds"] is JsonObject bounds)
        {
            TryAddAnnotation(node, bounds, display, target);
        }

        if (node["children"] is not JsonArray children)
        {
            return;
        }

        foreach (JsonNode? child in children)
        {
            if (child is JsonObject childObject)
            {
                CollectAnnotations(childObject, display, target, maxCount);
            }
        }
    }

    private static void TryAddAnnotation(
        JsonObject node, JsonObject bounds, DisplayInfo display, JsonArray target)
    {
        var box = new AnnotationBox(
            bounds["x"]?.GetValue<int>() ?? 0,
            bounds["y"]?.GetValue<int>() ?? 0,
            bounds["width"]?.GetValue<int>() ?? 0,
            bounds["height"]?.GetValue<int>() ?? 0);

        string? name = node["name"]?.GetValue<string>();
        string controlType = node["control_type"]?.GetValue<string>() ?? string.Empty;
        bool offscreen = node["offscreen"]?.GetValue<bool>() == true;

        if (offscreen || !IsInteresting(name, controlType) || !IsOnDisplay(box, display))
        {
            return;
        }

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

    private static bool IsInteresting(string? name, string controlType) =>
        !string.IsNullOrWhiteSpace(name) ||
        controlType is "Button" or "Edit" or "Document" or "ListItem"
            or "MenuItem" or "TabItem" or "ComboBox";

    private static bool IsOnDisplay(AnnotationBox box, DisplayInfo display) =>
        box.Width >= 8 && box.Height >= 8 &&
        box.X + box.Width > display.X && box.Y + box.Height > display.Y &&
        box.X < display.X + display.Width && box.Y < display.Y + display.Height;

    private readonly record struct AnnotationBox(int X, int Y, int Width, int Height);
}
