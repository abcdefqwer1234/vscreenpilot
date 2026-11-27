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
    [property: JsonPropertyName("dialogs")] IReadOnlyList<LaunchDialogView> Dialogs)
{
    /// <summary>窗口是否可见；false 表示进程已启动但没有窗口（控制台/启动器）。</summary>
    [JsonPropertyName("window")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Window { get; init; }

    /// <summary>没有窗口时，进程是否仍在运行。</summary>
    [JsonPropertyName("alive")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Alive { get; init; }

    /// <summary>没有窗口且进程已退出时的退出码。</summary>
    [JsonPropertyName("exit_code")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }

    /// <summary>detached：进程不挂在本会话 Job 下，daemon 崩溃/退出后仍存活。</summary>
    [JsonPropertyName("detached")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Detached { get; init; }

    /// <summary>fit=clamp 是否实际调整过窗口。</summary>
    [JsonPropertyName("fit_adjusted")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? FitAdjusted { get; init; }

    /// <summary>调整前的窗口可见边界。</summary>
    [JsonPropertyName("requested_bounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OptionalBoundsView? RequestedBounds { get; init; }

    /// <summary>调整后的窗口可见边界。</summary>
    [JsonPropertyName("applied_bounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OptionalBoundsView? AppliedBounds { get; init; }
}
