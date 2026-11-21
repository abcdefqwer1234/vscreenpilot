using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>`get_element_text` 的输出视图；字段顺序与既有响应一致。</summary>
public sealed record ElementTextView(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("refreshed")] bool Refreshed);
