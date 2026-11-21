using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>可选矩形：全 null 时序列化为 `{}`，与旧启动响应的空 bounds 一致。</summary>
public sealed record OptionalBoundsView(
    [property: JsonPropertyName("x")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? X,
    [property: JsonPropertyName("y")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Y,
    [property: JsonPropertyName("width")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Width,
    [property: JsonPropertyName("height")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Height);

public sealed record LaunchDialogView(
    [property: JsonPropertyName("hwnd")] long Hwnd,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("class")] string ClassName);

/// <summary>`launch_or_place_app` 的输出视图。</summary>
public sealed record LaunchOrPlaceView(
    [property: JsonPropertyName("launched")] bool Launched,
    [property: JsonPropertyName("new_instance")] bool NewInstance,
    [property: JsonPropertyName("reused")] bool Reused,
    [property: JsonPropertyName("maximized")] bool Maximized,
    [property: JsonPropertyName("close_on_release")] bool CloseOnRelease,
    [property: JsonPropertyName("hwnd")] long Hwnd,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("document_ready")] bool? DocumentReady,
    [property: JsonPropertyName("document_ready_reason")] string DocumentReadyReason,
    [property: JsonPropertyName("ensure_document")] bool EnsureDocument,
    [property: JsonPropertyName("bounds")] OptionalBoundsView Bounds,
    [property: JsonPropertyName("warnings")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<string>? Warnings,
    [property: JsonPropertyName("profile")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Profile,
    [property: JsonPropertyName("hint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Hint,
    [property: JsonPropertyName("dialogs")] IReadOnlyList<LaunchDialogView> Dialogs);
