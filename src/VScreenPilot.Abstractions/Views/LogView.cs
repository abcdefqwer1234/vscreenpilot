using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>
/// `get_log` 的输出视图。字段名与顺序必须与既有响应一致：
/// source / include_other_clients / entries / file / last_error / file_entries。
/// </summary>
public sealed record LogView(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("include_other_clients")] bool IncludeOtherClients,
    [property: JsonPropertyName("entries")] IReadOnlyList<LogEntryView> Entries,
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("last_error")] LogEntryView? LastError,
    [property: JsonPropertyName("file_entries")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<LogEntryView>? FileEntries);

/// <summary>单条日志。`client_id` 只在有值时出现（与旧实现一致）。</summary>
public sealed record LogEntryView(
    [property: JsonPropertyName("time")] string? Time,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("client_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ClientId);
