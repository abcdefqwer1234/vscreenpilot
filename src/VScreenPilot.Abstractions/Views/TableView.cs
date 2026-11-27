using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>`read_table` 的输出视图；rows 的键是列名，另带 `_row` 行号。</summary>
public sealed record TableView(
    [property: JsonPropertyName("table_ref")] string TableRef,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("columns")] IReadOnlyList<string> Columns,
    [property: JsonPropertyName("rows")] IReadOnlyList<IReadOnlyDictionary<string, string>> Rows,
    [property: JsonPropertyName("row_count")] int RowCount,
    [property: JsonPropertyName("column_count")] int ColumnCount,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("snapshot_id")] string SnapshotId);
