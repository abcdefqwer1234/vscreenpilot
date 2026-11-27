using VScreenPilot.Uia;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class TablePagingTests
{
    [Theory]
    [InlineData(0, 10, 25, 0, 10)]
    [InlineData(10, 10, 25, 10, 10)]
    [InlineData(20, 10, 25, 20, 5)]
    [InlineData(30, 10, 25, 25, 0)]
    [InlineData(5, 100, 25, 5, 20)]
    public void ResolvePageUsesRowStart(
        int rowStart, int maxRows, int rowCount, int expectedStart, int expectedCount)
    {
        (int start, int count) = UiaTableOps.ResolvePage(rowStart, maxRows, rowCount);

        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedCount, count);
    }
}
