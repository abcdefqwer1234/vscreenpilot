using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Session;
using VScreenPilot.Tools;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>Element 组：find_element / get_ui_tree / invoke_element / set_element_value / get_element_text。</summary>
internal sealed class ElementTools
{
    private readonly SessionManager _session;

    public ElementTools(SessionManager session)
    {
        _session = session;
    }

    public ToolResponse FindElement(JsonElement arguments)
    {
        string? name = OptionalString(arguments, "name");
        string? controlType = OptionalString(arguments, "control_type");
        string? automationId = OptionalString(arguments, "automation_id");
        if (controlType is not null && !VScreenPilot.Uia.UiaService.IsKnownControlType(controlType))
        {
            var supported = new JsonArray();

            foreach (string item in VScreenPilot.Uia.UiaService.SupportedControlTypes)
            {
                supported.Add(item);
            }

            throw new ToolException(
                "E_INVALID_ARG",
                $"control_type 无效: {controlType}",
                new JsonObject { ["field"] = "control_type", ["supported"] = supported },
                retryable: false,
                hint: "使用 UIA 控件类型英文名，例如 Button、Edit、Document",
                stage: "validate");
        }

        string nameMatch = OptionalString(arguments, "name_match") ?? "exact";
        int index = OptionalInt(arguments, "index", 0);
        bool all = OptionalBool(arguments, "all", false);
        int maxResults = OptionalInt(arguments, "max_results", 20);
        bool includeOffscreen = OptionalBool(arguments, "include_offscreen", false);
        int? pid = OptionalNullableInt(arguments, "pid");
        string? role = OptionalString(arguments, "role");
        string prefer = OptionalString(arguments, "prefer") ?? "auto";

        return ToolResponseWriter.FromView(_session.FindElement(
            name, controlType, automationId, nameMatch, index, all, maxResults,
            includeOffscreen, pid, role, prefer));
    }

    public ToolResponse GetUiTree(JsonElement arguments)
    {
        int? maxDepth = OptionalNullableInt(arguments, "max_depth");
        bool? interactiveOnly = OptionalNullableBool(arguments, "interactive_only");
        int? maxNodes = OptionalNullableInt(arguments, "max_nodes");
        string? root = OptionalString(arguments, "root");
        int? pid = OptionalNullableInt(arguments, "pid");
        string? format = OptionalString(arguments, "format");
        bool newSnapshot = OptionalBool(arguments, "new_snapshot", false);

        return ToolResponseWriter.FromView(
            _session.GetUiTree(maxDepth, interactiveOnly, maxNodes, root, pid, format, newSnapshot));
    }

    public ToolResponse InvokeElement(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "invoke_element 需要 ref");
        bool allowClick = OptionalBool(arguments, "allow_click", false);
        string? pattern = OptionalString(arguments, "pattern");

        return ToolResponseWriter.FromView(_session.InvokeElement(reference, allowClick, pattern));
    }

    public ToolResponse SetElementValue(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 ref");
        string value = OptionalString(arguments, "value")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 value");
        bool allowKeyboard = OptionalBool(arguments, "allow_keyboard", false);
        string? mode = OptionalString(arguments, "mode");
        bool allowDestructive = OptionalBool(arguments, "allow_destructive", false);

        return ToolResponseWriter.FromView(_session.SetElementValue(
            reference, value, allowKeyboard, mode, allowDestructive));
    }

    public ToolResponse GetElementText(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "get_element_text 需要 ref");
        int maxChars = OptionalInt(arguments, "max_chars", 4096);

        return ToolResponseWriter.FromView(_session.GetElementText(reference, maxChars));
    }
}
