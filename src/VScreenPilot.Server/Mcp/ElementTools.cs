using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Services;
using VScreenPilot.Tools;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Mcp;

/// <summary>Element 组：find_element / get_ui_tree / invoke_element / set_element_value / get_element_text。</summary>
internal sealed class ElementTools
{
    private readonly IElementService _elements;

    public ElementTools(IElementService elements) => _elements = elements;

    public ToolResponse FindElement(JsonElement arguments)
    {
        string? controlType = OptionalString(arguments, "control_type");
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

        return ToolResponseWriter.FromView(_elements.Find(new ElementQuery
        {
            Name = OptionalString(arguments, "name"),
            ControlType = controlType,
            AutomationId = OptionalString(arguments, "automation_id"),
            NameMatch = OptionalString(arguments, "name_match") ?? "exact",
            Index = OptionalInt(arguments, "index", 0),
            All = OptionalBool(arguments, "all", false),
            MaxResults = OptionalInt(arguments, "max_results", 20),
            IncludeOffscreen = OptionalBool(arguments, "include_offscreen", false),
            Pid = OptionalNullableInt(arguments, "pid"),
            Role = OptionalString(arguments, "role"),
            Prefer = OptionalString(arguments, "prefer") ?? "auto",
        }));
    }

    public ToolResponse GetUiTree(JsonElement arguments) =>
        ToolResponseWriter.FromView(_elements.GetUiTree(new UiTreeQuery
        {
            MaxDepth = OptionalNullableInt(arguments, "max_depth"),
            InteractiveOnly = OptionalNullableBool(arguments, "interactive_only"),
            MaxNodes = OptionalNullableInt(arguments, "max_nodes"),
            Root = OptionalString(arguments, "root"),
            Pid = OptionalNullableInt(arguments, "pid"),
            Format = OptionalString(arguments, "format"),
            NewSnapshot = OptionalBool(arguments, "new_snapshot", false),
        }));

    public ToolResponse InvokeElement(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "invoke_element 需要 ref");
        bool allowClick = OptionalBool(arguments, "allow_click", false);
        string? pattern = OptionalString(arguments, "pattern");

        return ToolResponseWriter.FromView(_elements.Invoke(reference, allowClick, pattern));
    }

    public ToolResponse SetElementValue(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 ref");
        string value = OptionalString(arguments, "value")
            ?? throw new ToolException("E_INVALID_ARG", "set_element_value 需要 value");

        return ToolResponseWriter.FromView(_elements.SetValue(
            reference,
            value,
            OptionalBool(arguments, "allow_keyboard", false),
            OptionalString(arguments, "mode"),
            OptionalBool(arguments, "allow_destructive", false)));
    }

    public ToolResponse GetElementText(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "get_element_text 需要 ref");
        int maxChars = OptionalInt(arguments, "max_chars", 4096);

        return ToolResponseWriter.FromView(_elements.GetText(reference, maxChars));
    }
}
