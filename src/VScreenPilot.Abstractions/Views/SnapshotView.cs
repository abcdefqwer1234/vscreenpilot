using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

public sealed record SnapshotWindowView(
    [property: JsonPropertyName("hwnd")] long Hwnd,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("exe")] string? Exe);

/// <summary>
/// `snapshot` 的输出视图。标量字段强类型；tree/dialogs/windows/foreground/annotations/
/// screenshot 是已经定型且随场景变化的子树，保留 JsonNode 原样输出。
/// </summary>
public sealed record SnapshotView(
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("root_ref")] string RootRef,
    [property: JsonPropertyName("tree")] JsonObject Tree,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("nodes")] int Nodes,
    [property: JsonPropertyName("visited")] int Visited,
    [property: JsonPropertyName("pruned")] int Pruned,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("interactive_only")] bool InteractiveOnly,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    [property: JsonPropertyName("max_nodes_effective")] int MaxNodesEffective,
    [property: JsonPropertyName("ref_capacity")] int RefCapacity,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("window")] SnapshotWindowView Window,
    [property: JsonPropertyName("privacy_redactions")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? PrivacyRedactions,
    [property: JsonPropertyName("empty_retry")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? EmptyRetry,
    [property: JsonPropertyName("empty_reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? EmptyReason,
    [property: JsonPropertyName("max_nodes_clamped")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? MaxNodesClamped,
    [property: JsonPropertyName("truncated_reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TruncatedReason,
    [property: JsonPropertyName("dialogs")] JsonNode? Dialogs,
    [property: JsonPropertyName("root_hwnd")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    long? RootHwnd,
    [property: JsonPropertyName("root_is_dialog")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? RootIsDialog,
    [property: JsonPropertyName("windows")] JsonNode? Windows,
    [property: JsonPropertyName("foreground")] JsonNode? Foreground,
    [property: JsonPropertyName("annotations")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonNode? Annotations,
    [property: JsonPropertyName("screenshot")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonNode? Screenshot,
    [property: JsonPropertyName("screenshot_path")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScreenshotPath,
    [property: JsonPropertyName("screenshot_uri")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScreenshotUri,
    [property: JsonPropertyName("screenshot_resource_uri")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScreenshotResourceUri,
    [property: JsonPropertyName("screenshot_error")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ScreenshotError,
    [property: JsonPropertyName("screenshot_preview")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    JsonNode? ScreenshotPreview);
