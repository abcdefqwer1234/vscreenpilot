using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Services;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

internal sealed partial class ElementOperations
{
    /// <summary>读取表格/网格为行对象；优先 GridPattern，回退 DataItem 树。</summary>
    public TableView ReadTable(TableQuery query)
    {
        DisplayInfo display = _apps.EnsureDisplay();
        TrackedApp app = _apps.FindApp(query.Pid)
            ?? throw new ToolException(
                "E_NO_TARGET",
                query.Pid is null
                    ? "没有已接管的应用窗口，请先 launch_or_place_app"
                    : $"pid={query.Pid} 不是本会话中存活的应用",
                new JsonObject { ["pid"] = query.Pid });
        _apps.RequireOnDisplay(display, app.Hwnd, "read_table");

        UiaElementInfo tableInfo;
        IntPtr tableHwnd;
        string? containerRef = !string.IsNullOrWhiteSpace(query.RootRef)
            ? query.RootRef
            : query.Within;

        if (!string.IsNullOrWhiteSpace(containerRef))
        {
            using UiaElementHandle container = _state.Resolver.Resolve(
                containerRef, display, out ElementRefEntry entry, out _);
            tableHwnd = entry.Hwnd;

            if (IsTable(container.Info.ControlTypeName))
            {
                tableInfo = container.Info;
            }
            else
            {
                List<UiaElementInfo> found = FindTablesInContainer(container, query);
                tableInfo = Pick(found, query.Index, $"表格「{query.Name ?? containerRef}」");
            }
        }
        else
        {
            var candidates = new List<(UiaElementInfo Info, IntPtr Hwnd)>();

            foreach (WindowInfo window in WindowManager.ListForProcess(app.Pid))
            {
                foreach (UiaElementInfo info in FindTablesInWindow(window.Hwnd, query))
                {
                    candidates.Add((info, window.Hwnd));
                }
            }

            if (candidates.Count == 0)
            {
                throw new ToolException(
                    "E_ELEMENT_NOT_FOUND",
                    "未找到表格/网格控件",
                    new JsonObject
                    {
                        ["name"] = query.Name,
                        ["control_type"] = query.ControlType,
                        ["pid"] = query.Pid,
                    },
                    retryable: true,
                    hint: "先用 find_element(control_type=Table/DataGrid) 确认表格是否存在",
                    stage: "uia");
            }

            candidates = candidates
                .OrderByDescending(item => item.Info.Width * item.Info.Height)
                .ToList();
            (tableInfo, tableHwnd) = candidates[Math.Clamp(query.Index, 0, candidates.Count - 1)];
        }

        using UiaElementHandle table = _state.Uia.FindByRuntimeId(tableHwnd, tableInfo.RuntimeId)
            ?? throw new ToolException(
                "E_ELEMENT_STALE",
                "表格控件已失效，请重新 snapshot/find_element",
                new JsonObject { ["name"] = tableInfo.Name },
                retryable: true,
                hint: "重新 snapshot 或 find_element 后再试",
                stage: "uia");

        int rowStart = Math.Max(0, query.RowStart);
        UiaTableData data = table.ReadTable(
            Math.Clamp(query.MaxRows, 1, 500), Math.Clamp(query.MaxCells, 1, 20_000), rowStart);

        if (data.RowCount == 0 || data.Rows.Count == 0)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                "表格没有可读行（provider 未暴露 Grid/Table/DataItem，或 row_start 已越过末尾）",
                new JsonObject { ["name"] = tableInfo.Name, ["row_start"] = rowStart },
                retryable: true,
                hint: "改用 find_element(all=true) 读 DataItem 的 value，或减小 row_start",
                stage: "uia");
        }

        // 表头可能为空、重复或少于实际列数；统一补齐到 ColumnCount，避免单元格被跳过或覆盖。
        List<string> allColumns = UiaTableOps.BuildColumnNames(data);
        List<int> selected = ResolveColumns(allColumns, query.Columns);
        var rows = new List<IReadOnlyDictionary<string, string>>();

        foreach (IReadOnlyList<UiaTableCell> row in data.Rows.Take(Math.Clamp(query.MaxRows, 1, 500)))
        {
            int rowIndex = rowStart + rows.Count;
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["_row"] = rowIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            foreach (UiaTableCell cell in row)
            {
                if (!selected.Contains(cell.Column) || cell.Column >= allColumns.Count)
                {
                    continue;
                }

                values[allColumns[cell.Column]] = cell.Value;
            }

            rows.Add(values);
        }

        string snapshotId = _snapshot.EnsureSnapshot();

        return new TableView(
            _state.RegisterElement(tableInfo, tableHwnd),
            tableInfo.Name,
            tableInfo.ControlTypeName,
            data.Source,
            query.IncludeHeader
                ? selected.Select(index => allColumns[index]).ToList()
                : new List<string>(),
            rows,
            data.RowCount,
            data.ColumnCount,
            data.Truncated || rowStart > 0 || data.RowCount - rowStart > rows.Count,
            snapshotId);
    }

    private static bool IsTable(string controlType) =>
        controlType is "Table" or "DataGrid" or "List";

    private static UiaElementInfo Pick(List<UiaElementInfo> items, int index, string label)
    {
        if (items.Count == 0)
        {
            throw new ToolException(
                "E_ELEMENT_NOT_FOUND",
                $"未找到{label}",
                retryable: true,
                hint: "确认 root_ref 指向表格或其容器",
                stage: "uia");
        }

        return items[Math.Clamp(index, 0, items.Count - 1)];
    }

    private List<UiaElementInfo> FindTablesInContainer(UiaElementHandle container, TableQuery query)
    {
        UiaNameMatch mode = ParseNameMatch(query.NameMatch);
        var result = new List<UiaElementInfo>();

        foreach (string type in TableTypes(query.ControlType))
        {
            UiaFindResult found = container.FindDescendants(
                query.Name, mode, type, query.AutomationId, 50);

            if (found.Items.Count > 0)
            {
                result.AddRange(found.Items);
                break;
            }
        }

        return result;
    }

    private List<UiaElementInfo> FindTablesInWindow(IntPtr hwnd, TableQuery query)
    {
        UiaNameMatch mode = ParseNameMatch(query.NameMatch);
        var result = new List<UiaElementInfo>();

        foreach (string type in TableTypes(query.ControlType))
        {
            UiaFindResult found;

            try
            {
                found = _state.Uia.FindElementsDetailed(
                    hwnd, query.Name, mode, type, query.AutomationId, 50);
            }
            catch (COMException)
            {
                continue;
            }

            if (found.Items.Count > 0)
            {
                result.AddRange(found.Items);
                break;
            }
        }

        return result;
    }

    private static IEnumerable<string> TableTypes(string? requested) =>
        string.IsNullOrWhiteSpace(requested)
            ? new[] { "Table", "DataGrid", "List" }
            : new[] { requested };

    private static UiaNameMatch ParseNameMatch(string mode) => mode.ToLowerInvariant() switch
    {
        "contains" => UiaNameMatch.Contains,
        "regex" => UiaNameMatch.Regex,
        _ => UiaNameMatch.Exact,
    };

    private static List<int> ResolveColumns(
        IReadOnlyList<string> available, IReadOnlyList<string>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return Enumerable.Range(0, available.Count).ToList();
        }

        var selected = new List<int>();

        foreach (string name in requested)
        {
            int index = -1;

            for (int candidate = 0; candidate < available.Count; candidate++)
            {
                if (string.Equals(available[candidate], name, StringComparison.OrdinalIgnoreCase))
                {
                    index = candidate;
                    break;
                }
            }

            if (index < 0)
            {
                throw new ToolException(
                    "E_INVALID_ARG",
                    $"列不存在: {name}",
                    new JsonObject
                    {
                        ["requested"] = name,
                        ["available"] = new JsonArray(available.Select(item => (JsonNode)item).ToArray()),
                    },
                    retryable: false,
                    hint: "用返回的 columns 或先不传 columns 读取表头",
                    stage: "validate");
            }

            if (!selected.Contains(index))
            {
                selected.Add(index);
            }
        }

        return selected;
    }

}
