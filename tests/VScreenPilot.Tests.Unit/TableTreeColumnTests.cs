using VScreenPilot.Uia;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class TableTreeColumnTests
{
    private static UiaElementInfo Cell(string name, int left) =>
        new("rt", name, string.Empty, 1, "DataItem", left, 0, left + 80, 20, true, false, 1, name);

    [Fact]
    public void MissingCellDoesNotShiftLaterColumns()
    {
        var rows = new List<List<UiaElementInfo>>
        {
            new() { Cell("数值", 0), Cell("标题", 100), Cell("状态", 200) },
            new() { Cell("0", 0), Cell("无", 200) },
        };

        List<UiaTableOps.ColumnAnchor> anchors = UiaTableOps.BuildColumnAnchors(rows);

        Assert.Equal(3, anchors.Count);
        Assert.Equal("数值", anchors[0].Name);
        Assert.Equal("标题", anchors[1].Name);
        Assert.Equal("状态", anchors[2].Name);
        Assert.Equal(2, UiaTableOps.ResolveColumn(rows[1][1], anchors));
    }

    [Fact]
    public void ColumnNamesPadEmptyAndDeduplicateRepeatedHeaders()
    {
        var data = new UiaTableData(
            new[] { string.Empty, "值", "值" },
            Array.Empty<IReadOnlyList<UiaTableCell>>(),
            RowCount: 0,
            ColumnCount: 5,
            Truncated: false,
            Source: "test");

        List<string> names = UiaTableOps.BuildColumnNames(data);

        Assert.Equal(["col0", "值", "值#2", "col3", "col4"], names);
    }

    [Fact]
    public void ColumnNamesDeriveCountFromRowsWhenProviderReportsNone()
    {
        var cell = new UiaTableCell(0, 2, "x", "x", "DataItem");
        var data = new UiaTableData(
            Array.Empty<string>(),
            new[] { (IReadOnlyList<UiaTableCell>)new[] { cell } },
            RowCount: 1,
            ColumnCount: 0,
            Truncated: false,
            Source: "test");

        Assert.Equal(["col0", "col1", "col2"], UiaTableOps.BuildColumnNames(data));
    }
}
