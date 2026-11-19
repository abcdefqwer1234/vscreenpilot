using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>
/// `release_environment` 的领域视图。Server 会在编码后额外补 `client_id`、
/// `display_kept_reason`、`screen_lease_released`——那三项属于调度状态，不属于领域。
/// </summary>
public sealed record ReleaseReport(
    [property: JsonPropertyName("released")] bool Released,
    [property: JsonPropertyName("display_removed")] bool DisplayRemoved,
    [property: JsonPropertyName("display_kept")] bool DisplayKept,
    [property: JsonPropertyName("on_dirty")] string OnDirty,
    [property: JsonPropertyName("restored_pids")] IReadOnlyList<int> RestoredPids,
    [property: JsonPropertyName("closed_pids")] IReadOnlyList<int> ClosedPids,
    [property: JsonPropertyName("forced_pids")] IReadOnlyList<int> ForcedPids,
    [property: JsonPropertyName("kept_pids")] IReadOnlyList<int> KeptPids,
    [property: JsonPropertyName("saved_pids")] IReadOnlyList<int> SavedPids,
    [property: JsonPropertyName("discarded_pids")] IReadOnlyList<int> DiscardedPids,
    [property: JsonPropertyName("memory")] MemoryView Memory,
    [property: JsonPropertyName("warnings")] IReadOnlyList<string> Warnings);

/// <summary>
/// 内存裁剪结果。`private_mb` 只在“工作集已经足够小、直接返回”的分支出现，
/// 因此必须可选且序列化在最后，保持与旧输出一致的字段集。
/// </summary>
public sealed record MemoryView(
    [property: JsonPropertyName("before_mb")] double BeforeMb,
    [property: JsonPropertyName("after_gc_mb")] double AfterGcMb,
    [property: JsonPropertyName("after_mb")] double AfterMb,
    [property: JsonPropertyName("trimmed")] bool Trimmed,
    [property: JsonPropertyName("emptied")] bool Emptied,
    [property: JsonPropertyName("private_mb")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? PrivateMb = null);
