using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

public sealed record BoundsView(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>
/// UIA 元素视图：同时覆盖完整与 compact 两种形状；compact 时省略诊断字段，
/// 并按旧实现只在 false/true 时输出 enabled/offscreen。
/// </summary>
public sealed record ElementView(
    [property: JsonPropertyName("runtime_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RuntimeId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("control_type_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ControlTypeId,
    [property: JsonPropertyName("bounds")] BoundsView Bounds,
    [property: JsonPropertyName("enabled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Enabled,
    [property: JsonPropertyName("offscreen")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Offscreen,
    [property: JsonPropertyName("process_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ProcessId,
    [property: JsonPropertyName("class_name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ClassName,
    [property: JsonPropertyName("focusable")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Focusable,
    [property: JsonPropertyName("focused")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Focused,
    [property: JsonPropertyName("value")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Value,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Text);

/// <summary>`invoke_element` 的输出视图；msaa/uia/message/default 四个分支共用。</summary>
public sealed record InvokeElementView(
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("invoked")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Invoked,
    [property: JsonPropertyName("verified")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Verified,
    [property: JsonPropertyName("control_type")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ControlType,
    [property: JsonPropertyName("refreshed")] bool Refreshed,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail,
    [property: JsonPropertyName("element")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ElementView? Element);
