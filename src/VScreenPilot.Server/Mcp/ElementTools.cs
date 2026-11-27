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
            Within = OptionalString(arguments, "within"),
            NewSnapshot = OptionalBool(arguments, "new_snapshot", false),
            Scope = OptionalString(arguments, "scope") ?? "app",
        }));
    }

    public ToolResponse ReadTable(JsonElement arguments)
    {
        IReadOnlyList<string>? columns = null;

        if (arguments.TryGetProperty("columns", out JsonElement columnsElement))
        {
            if (columnsElement.ValueKind != JsonValueKind.Array)
            {
                throw new ToolException("E_INVALID_ARG", "columns 必须是字符串数组");
            }

            var values = new List<string>();

            foreach (JsonElement item in columnsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ToolException("E_INVALID_ARG", "columns 必须是字符串数组");
                }

                values.Add(item.GetString() ?? string.Empty);
            }

            columns = values;
        }

        return ToolResponseWriter.FromView(_elements.ReadTable(new TableQuery
        {
            RootRef = OptionalString(arguments, "root_ref"),
            Within = OptionalString(arguments, "within"),
            Name = OptionalString(arguments, "name"),
            NameMatch = OptionalString(arguments, "name_match") ?? "exact",
            ControlType = OptionalString(arguments, "control_type"),
            AutomationId = OptionalString(arguments, "automation_id"),
            Pid = OptionalNullableInt(arguments, "pid"),
            Index = OptionalInt(arguments, "index", 0),
            MaxRows = OptionalInt(arguments, "max_rows", 100),
            RowStart = OptionalInt(arguments, "row_start", 0),
            IncludeHeader = OptionalBool(arguments, "include_header", true),
            Columns = columns,
            MaxCells = OptionalInt(arguments, "max_cells", 5000),
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
        bool allowDestructive = OptionalBool(arguments, "allow_destructive", false);
        string? pattern = OptionalString(arguments, "pattern");
        string? selectText = OptionalString(arguments, "select_text");

        return ToolResponseWriter.FromView(
            _elements.Invoke(reference, allowClick, pattern, selectText, allowDestructive));
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
            OptionalBool(arguments, "allow_destructive", false),
            OptionalBool(arguments, "strict_verify", false)));
    }

    public ToolResponse GetElementText(JsonElement arguments)
    {
        string reference = OptionalString(arguments, "ref")
            ?? throw new ToolException("E_INVALID_ARG", "get_element_text 需要 ref");
        int maxChars = OptionalInt(arguments, "max_chars", 4096);

        return ToolResponseWriter.FromView(_elements.GetText(reference, maxChars));
    }

    public ToolResponse SelectTab(JsonElement arguments)
    {
        string name = OptionalString(arguments, "name")
            ?? throw new ToolException("E_INVALID_ARG", "select_tab 需要 name");

        return ToolResponseWriter.FromView(_elements.SelectTab(
            name,
            OptionalString(arguments, "name_match") ?? "exact",
            OptionalInt(arguments, "index", 0),
            OptionalNullableInt(arguments, "pid")));
    }

    public ToolResponse CloseTab(JsonElement arguments)
    {
        string name = OptionalString(arguments, "name")
            ?? throw new ToolException("E_INVALID_ARG", "close_tab 需要 name");

        return ToolResponseWriter.FromView(_elements.CloseTab(
            name,
            OptionalString(arguments, "name_match") ?? "exact",
            OptionalInt(arguments, "index", 0),
            OptionalNullableInt(arguments, "pid"),
            OptionalBool(arguments, "allow_destructive", false)));
    }
}
