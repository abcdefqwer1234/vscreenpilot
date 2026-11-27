using System.Runtime.InteropServices;
using Interop.UIAutomationClient;

namespace VScreenPilot.Uia;

internal sealed record UiaTableCell(
    int Row, int Column, string Name, string Value, string ControlType);

internal sealed record UiaTableData(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<UiaTableCell>> Rows,
    int RowCount,
    int ColumnCount,
    bool Truncated,
    string Source);

/// <summary>
/// 表格读取：优先 GridPattern（行/列/GetItem），TablePattern 取列头；
/// 不支持 pattern 的自绘表格回退到 DataItem 后代按几何位置分组。
/// </summary>
internal sealed class UiaTableOps
{
    private const int GridPatternId = 10006;
    private const int TablePatternId = 10012;

    private readonly UiaService _core;

    public UiaTableOps(UiaService core) => _core = core;

    public UiaTableData Read(IUIAutomationElement table, int maxRows, int maxCells, int rowStart = 0)
    {
        lock (_core._sync)
        {
            UiaTableData? grid = ReadGrid(table, maxRows, maxCells, rowStart);
            return grid ?? ReadTree(table, maxRows, maxCells, rowStart)
                ?? new UiaTableData([], [], 0, 0, false, "none");
        }
    }

    private UiaTableData? ReadGrid(
        IUIAutomationElement table, int maxRows, int maxCells, int rowStart)
    {
        object? gridPattern = null;

        try
        {
            gridPattern = table.GetCurrentPattern(GridPatternId);

            if (gridPattern is not IUIAutomationGridPattern grid)
            {
                return null;
            }

            int rowCount = grid.CurrentRowCount;
            int columnCount = grid.CurrentColumnCount;

            if (rowCount <= 0 || columnCount <= 0)
            {
                return null;
            }

            List<string> headers = ReadHeaders(table, columnCount);
            (int start, int rowsToRead) = ResolvePage(rowStart, maxRows, rowCount);

            if (rowsToRead <= 0)
            {
                return new UiaTableData(headers, [], rowCount, columnCount, true, "grid_pattern");
            }

            int cellsToRead = Math.Min(rowsToRead * columnCount, Math.Max(1, maxCells));
            var rows = new List<IReadOnlyList<UiaTableCell>>();
            int read = 0;
            bool truncated = rowCount > start + rowsToRead || rowsToRead * columnCount > cellsToRead;

            for (int row = start; row < start + rowsToRead && read < cellsToRead; row++)
            {
                var cells = new List<UiaTableCell>();

                for (int column = 0; column < columnCount && read < cellsToRead; column++)
                {
                    IUIAutomationElement? cell = null;

                    try
                    {
                        cell = grid.GetItem(row, column);
                        UiaElementInfo? info = cell is null
                            ? null
                            : _core.Factory.Describe(cell, cached: false);
                        string value = cell is null
                            ? string.Empty
                            : _core.GetValue(cell) ?? info?.Value ?? info?.Name ?? string.Empty;
                        cells.Add(new UiaTableCell(
                            row,
                            column,
                            info?.Name ?? string.Empty,
                            value,
                            info?.ControlTypeName ?? string.Empty));
                    }
                    catch (COMException)
                    {
                        cells.Add(new UiaTableCell(row, column, string.Empty, string.Empty, string.Empty));
                    }
                    finally
                    {
                        UiaElementFactoryOps.ReleaseElement(cell);
                    }

                    read++;
                }

                rows.Add(cells);
            }

            return new UiaTableData(
                headers, rows, rowCount, columnCount, truncated, "grid_pattern");
        }
        catch (COMException)
        {
            return null;
        }
        catch (InvalidCastException)
        {
            return null;
        }
        finally
        {
            UiaElementFactoryOps.ReleaseElement(gridPattern);
        }
    }

    private List<string> ReadHeaders(IUIAutomationElement table, int columnCount)
    {
        var headers = new List<string>();
        object? tablePattern = null;
        IUIAutomationElementArray? array = null;

        try
        {
            tablePattern = table.GetCurrentPattern(TablePatternId);

            if (tablePattern is not IUIAutomationTablePattern tableHeaders)
            {
                return headers;
            }

            array = tableHeaders.GetCurrentColumnHeaders();

            if (array is null)
            {
                return headers;
            }

            int count = Math.Min(columnCount, array.Length);

            for (int index = 0; index < count; index++)
            {
                IUIAutomationElement? header = null;

                try
                {
                    header = array.GetElement(index);
                    UiaElementInfo? info = header is null
                        ? null
                        : _core.Factory.Describe(header, cached: false);
                    headers.Add(info?.Name ?? string.Empty);
                }
                catch (COMException)
                {
                    headers.Add(string.Empty);
                }
                finally
                {
                    UiaElementFactoryOps.ReleaseElement(header);
                }
            }

            return headers;
        }
        catch (COMException)
        {
            return headers;
        }
        finally
        {
            UiaElementFactoryOps.ReleaseElement(array);
            UiaElementFactoryOps.ReleaseElement(tablePattern);
        }
    }

    private UiaTableData ReadTree(
        IUIAutomationElement table, int maxRows, int maxCells, int rowStart)
    {
        UiaFindResult cells;

        try
        {
            cells = _core.FindDescendants(
                table, null, UiaNameMatch.Exact, "DataItem", null, Math.Max(1, maxCells));
        }
        catch (COMException)
        {
            return new UiaTableData([], [], 0, 0, false, "tree");
        }

        if (cells.Items.Count == 0)
        {
            return new UiaTableData([], [], 0, 0, false, "tree");
        }

        var rows = new List<List<UiaElementInfo>>();

        foreach (UiaElementInfo cell in cells.Items.OrderBy(item => item.Top).ThenBy(item => item.Left))
        {
            List<UiaElementInfo>? row = rows.LastOrDefault();
            int tolerance = Math.Max(4, cell.Height / 2);

            if (row is null || row.Count == 0 || Math.Abs(cell.Top - row[0].Top) > tolerance)
            {
                row = new List<UiaElementInfo>();
                rows.Add(row);
            }

            row.Add(cell);
        }

        if (rows.Count == 0)
        {
            return new UiaTableData([], [], 0, 0, false, "tree");
        }

        List<List<UiaElementInfo>> allRows = rows
            .OrderBy(row => row.Min(cell => cell.Top))
            .ToList();
        List<ColumnAnchor> anchors = BuildColumnAnchors(allRows);
        int columnCount = anchors.Count;
        var columns = new List<string>();

        for (int column = 0; column < columnCount; column++)
        {
            string name = anchors[column].Name;
            columns.Add(string.IsNullOrWhiteSpace(name) ? $"col{column}" : name);
        }

        (int start, int rowsToRead) = ResolvePage(rowStart, maxRows, allRows.Count);
        List<List<UiaElementInfo>> page = allRows.Skip(start).Take(rowsToRead).ToList();
        int cellsToRead = Math.Min(rowsToRead * columnCount, Math.Max(1, maxCells));
        var resultRows = new List<IReadOnlyList<UiaTableCell>>();
        int read = 0;

        foreach (List<UiaElementInfo> row in page)
        {
            var cellsInRow = new List<UiaTableCell>();
            var usedColumns = new HashSet<int>();

            foreach (UiaElementInfo cell in row.OrderBy(item => item.Left))
            {
                if (read >= cellsToRead)
                {
                    break;
                }

                int column = ResolveColumn(cell, anchors);
                if (!usedColumns.Add(column))
                {
                    continue;
                }

                string value = string.IsNullOrEmpty(cell.Value) ? cell.Name : cell.Value;
                cellsInRow.Add(new UiaTableCell(
                    resultRows.Count, column, cell.Name, value, cell.ControlTypeName));
                read++;
            }

            resultRows.Add(cellsInRow);
        }

        return new UiaTableData(
            columns, resultRows, allRows.Count, columnCount,
            cells.Truncated || start > 0 || allRows.Count > start + page.Count, "tree");
    }

    internal sealed record ColumnAnchor(int Left, string Name);

    /// <summary>
    /// 用所有行的左边界聚类出列锚点，避免“某行缺一个 cell”时后续列整体左移。
    /// 表头名优先取第一行中距锚点最近的 cell。
    /// </summary>
    internal static List<ColumnAnchor> BuildColumnAnchors(List<List<UiaElementInfo>> rows)
    {
        var anchors = new List<ColumnAnchor>();
        const int tolerance = 8;

        foreach (List<UiaElementInfo> row in rows)
        {
            foreach (UiaElementInfo cell in row)
            {
                if (anchors.Any(anchor => Math.Abs(anchor.Left - cell.Left) <= tolerance))
                {
                    continue;
                }

                anchors.Add(new ColumnAnchor(cell.Left, string.Empty));
            }
        }

        anchors = anchors.OrderBy(anchor => anchor.Left).ToList();
        List<UiaElementInfo> firstRow = rows[0];

        for (int index = 0; index < anchors.Count; index++)
        {
            ColumnAnchor anchor = anchors[index];
            UiaElementInfo? header = firstRow
                .OrderBy(cell => Math.Abs(cell.Left - anchor.Left))
                .FirstOrDefault(cell => Math.Abs(cell.Left - anchor.Left) <= tolerance);

            anchors[index] = anchor with { Name = header?.Name ?? string.Empty };
        }

        return anchors;
    }

    internal static int ResolveColumn(UiaElementInfo cell, List<ColumnAnchor> anchors)
    {
        int best = 0;
        int bestDistance = int.MaxValue;

        for (int index = 0; index < anchors.Count; index++)
        {
            int distance = Math.Abs(anchors[index].Left - cell.Left);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = index;
            }
        }

        return best;
    }

    /// <summary>按 row_start/max_rows 计算实际读取窗口；供 Grid/Tree 两条路径共用。</summary>
    internal static (int Start, int Count) ResolvePage(int rowStart, int maxRows, int rowCount)
    {
        int start = Math.Clamp(rowStart, 0, Math.Max(0, rowCount));
        int count = Math.Min(Math.Max(1, maxRows), Math.Max(0, rowCount - start));
        return (start, count);
    }

    /// <summary>
    /// 把 provider 返回的表头规范化为与实际列数一致、且可安全作为字典键的名称。
    /// 空表头补 colN，短表头补齐，重复表头追加 #2/#3，避免 row 对象互相覆盖。
    /// </summary>
    internal static List<string> BuildColumnNames(UiaTableData data)
    {
        int count = Math.Max(data.ColumnCount, data.Columns.Count);

        if (count == 0)
        {
            count = data.Rows
                .SelectMany(row => row)
                .Select(cell => cell.Column + 1)
                .DefaultIfEmpty(0)
                .Max();
        }

        var names = new List<string>(count);
        var used = new HashSet<string>(StringComparer.Ordinal);

        for (int index = 0; index < count; index++)
        {
            string raw = index < data.Columns.Count
                ? (data.Columns[index] ?? string.Empty).Trim()
                : string.Empty;
            string baseName = raw.Length == 0 ? $"col{index}" : raw;
            string name = baseName;
            int suffix = 2;

            while (!used.Add(name))
            {
                name = $"{baseName}#{suffix++}";
            }

            names.Add(name);
        }

        return names;
    }
}
