using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>`find_element(all=true)` 的单个条目：compact 元素字段 + ref。</summary>
public sealed record FindElementItemView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("bounds")] BoundsView Bounds,
    [property: JsonPropertyName("enabled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Enabled,
    [property: JsonPropertyName("offscreen")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Offscreen,
    [property: JsonPropertyName("focused")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Focused,
    [property: JsonPropertyName("value")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Value,
    [property: JsonPropertyName("ref")] string Ref);

/// <summary>`find_element(all=true)` 的输出视图。</summary>
public sealed record FindElementsView(
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("name_match")] string NameMatch,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("prefer")] string Prefer,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("elements")] IReadOnlyList<FindElementItemView> Elements);

/// <summary>`find_element` 的单元素输出视图。</summary>
public sealed record FindElementView(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("automation_id")] string AutomationId,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("bounds")] BoundsView Bounds,
    [property: JsonPropertyName("enabled")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Enabled,
    [property: JsonPropertyName("offscreen")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Offscreen,
    [property: JsonPropertyName("focused")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Focused,
    [property: JsonPropertyName("value")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Value,
    [property: JsonPropertyName("ref")] string Ref,
    [property: JsonPropertyName("name_match")] string NameMatch,
    [property: JsonPropertyName("role")] string? Role,
    [property: JsonPropertyName("prefer")] string Prefer,
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId);

public sealed record TreeWindowView(
    [property: JsonPropertyName("hwnd")] long Hwnd,
    [property: JsonPropertyName("pid")] int Pid,
    [property: JsonPropertyName("title")] string Title);

/// <summary>`get_ui_tree` 的输出视图；UIA 与 MSAA 两种后端共用。</summary>
public sealed record UiTreeView(
    [property: JsonPropertyName("root_ref")] string RootRef,
    [property: JsonPropertyName("tree")] JsonObject Tree,
    [property: JsonPropertyName("nodes")] int Nodes,
    [property: JsonPropertyName("visited")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Visited,
    [property: JsonPropertyName("pruned")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Pruned,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("interactive_only")] bool InteractiveOnly,
    [property: JsonPropertyName("max_depth")] int MaxDepth,
    [property: JsonPropertyName("max_depth_reached")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MaxDepthReached,
    [property: JsonPropertyName("elapsed_ms")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? ElapsedMs,
    [property: JsonPropertyName("ref_capacity")] int RefCapacity,
    [property: JsonPropertyName("max_nodes_effective")] int MaxNodesEffective,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId,
    [property: JsonPropertyName("new_snapshot")] bool NewSnapshot,
    [property: JsonPropertyName("backend")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Backend,
    [property: JsonPropertyName("max_nodes_clamped")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? MaxNodesClamped,
    [property: JsonPropertyName("truncated_reason")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TruncatedReason,
    [property: JsonPropertyName("text")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Text,
    [property: JsonPropertyName("window")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TreeWindowView? Window);
