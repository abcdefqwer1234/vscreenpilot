using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>动作后的轻量屏幕观察（16×16 网格签名对比）。</summary>
public sealed record ObservedView(
    [property: JsonPropertyName("changed")] bool Changed,
    [property: JsonPropertyName("ratio")] double Ratio,
    [property: JsonPropertyName("threshold")] double Threshold,
    [property: JsonPropertyName("grid")] int Grid,
    [property: JsonPropertyName("before")] string Before,
    [property: JsonPropertyName("after")] string After,
    [property: JsonPropertyName("ms")] double Ms,
    [property: JsonPropertyName("region")] RegionView Region);

public sealed record PointView(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y);

public sealed record RegionView(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>点击前命中测试得到的元素；用于安全护栏与结果溯源。</summary>
public sealed record HitElementView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("bounds")] BoundsView Bounds,
    [property: JsonPropertyName("enabled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Enabled,
    [property: JsonPropertyName("destructive")] bool Destructive);

/// <summary>`select_tab` / `close_tab` 的输出视图。</summary>
public sealed record TabActionView(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("closed")] bool Closed,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("bounds")] BoundsView Bounds);

/// <summary>`press_keys` / `press_key_sequence` 的输出视图。</summary>
public sealed record PressKeysView(
    [property: JsonPropertyName("keys")] string Keys,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("target_pid")] int TargetPid,
    [property: JsonPropertyName("inject_ms")] double InjectMs,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("keep_foreground")] bool KeepForeground,
    [property: JsonPropertyName("observed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ObservedView? Observed,
    [property: JsonPropertyName("verified")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Verified);

/// <summary>`type_text` 的输出视图。</summary>
public sealed record TypeTextView(
    [property: JsonPropertyName("typed")] int Typed,
    [property: JsonPropertyName("length")] int Length,
    [property: JsonPropertyName("target_pid")] int TargetPid,
    [property: JsonPropertyName("chunks")] int Chunks,
    [property: JsonPropertyName("chunk_chars")] int ChunkChars,
    [property: JsonPropertyName("text_supported")] bool TextSupported,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("method")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Method,
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("inject_ms")] double InjectMs,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("observed_length")]
    int? ObservedLength,
    [property: JsonPropertyName("warning")]
    string? Warning);

/// <summary>`gesture` 的输出视图；drag/scroll/move 共用，未用字段省略。</summary>
public sealed record GestureView(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("from")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PointView? From,
    [property: JsonPropertyName("to")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PointView? To,
    [property: JsonPropertyName("dx")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Dx,
    [property: JsonPropertyName("dy")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Dy,
    [property: JsonPropertyName("button")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Button,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("inject_ms")] double InjectMs,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("hint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Hint,
    [property: JsonPropertyName("observed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ObservedView? Observed);

/// <summary>会话级输入/前台租约。</summary>
public sealed record InputLeaseView(
    [property: JsonPropertyName("lease_id")] string LeaseId,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("hwnd")] long Hwnd,
    [property: JsonPropertyName("expires_in_ms")] long ExpiresInMs,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("exclusive")] bool Exclusive,
    [property: JsonPropertyName("source")] string Source);

/// <summary>多击期间检测到的新顶层窗口。</summary>
public sealed record OpenedDialogView(
    [property: JsonPropertyName("hwnd")] long Hwnd,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("class")] string ClassName);

/// <summary>`click` 的输出视图。</summary>
public sealed record ClickView(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("button")] string Button,
    [property: JsonPropertyName("clicks")] int Clicks,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("inject_ms")] double InjectMs,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("keep_foreground")] bool KeepForeground,
    [property: JsonPropertyName("observed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ObservedView? Observed,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail,
    [property: JsonPropertyName("hint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Hint,
    [property: JsonPropertyName("hit_element")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    HitElementView? HitElement = null,
    [property: JsonPropertyName("hit_source")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? HitSource = null,
    [property: JsonPropertyName("focus_before")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FocusElementView? FocusBefore = null,
    [property: JsonPropertyName("focus_after")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    FocusElementView? FocusAfter = null,
    [property: JsonPropertyName("focus_changed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? FocusChanged = null,
    [property: JsonPropertyName("delivery")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Delivery = null,
    [property: JsonPropertyName("suspect_noop")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? SuspectNoop = null,
    [property: JsonPropertyName("clicks_done")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? ClicksDone = null,
    [property: JsonPropertyName("aborted")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Aborted = null,
    [property: JsonPropertyName("opened_dialog")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OpenedDialogView? OpenedDialog = null);

/// <summary>点击前后焦点元素；用于区分“没点中”和“命中但无视觉变化”。</summary>
public sealed record FocusElementView(
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("runtime_id")] string RuntimeId);

/// <summary>动作后对元素本身的轻量观察（click_ref 使用）。</summary>
public sealed record ElementObservedView(
    [property: JsonPropertyName("present")] bool? Present,
    [property: JsonPropertyName("enabled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Enabled,
    [property: JsonPropertyName("focused")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Focused,
    [property: JsonPropertyName("offscreen")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Offscreen,
    [property: JsonPropertyName("error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Error);

/// <summary>`click_ref` 的输出视图；msaa / uia / 坐标降级三条分支共用。</summary>
public sealed record ClickRefView(
    [property: JsonPropertyName("clicked")] bool Clicked,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("channel")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Channel,
    [property: JsonPropertyName("x")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? X,
    [property: JsonPropertyName("y")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Y,
    [property: JsonPropertyName("button")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Button,
    [property: JsonPropertyName("clicks")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Clicks,
    [property: JsonPropertyName("mode")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Mode,
    [property: JsonPropertyName("method")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Method,
    [property: JsonPropertyName("pattern")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Pattern,
    [property: JsonPropertyName("inject_ms")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? InjectMs,
    [property: JsonPropertyName("duration_ms")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? DurationMs,
    [property: JsonPropertyName("verified")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Verified,
    [property: JsonPropertyName("keep_foreground")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? KeepForeground,
    [property: JsonPropertyName("refreshed")] bool Refreshed,
    [property: JsonPropertyName("observed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ElementObservedView? Observed,
    [property: JsonPropertyName("bounds")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OptionalBoundsView? Bounds,
    [property: JsonPropertyName("detail")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Detail,
    [property: JsonPropertyName("hint")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Hint,
    [property: JsonPropertyName("hit_element")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    HitElementView? HitElement = null,
    [property: JsonPropertyName("suspect_noop")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? SuspectNoop = null);
