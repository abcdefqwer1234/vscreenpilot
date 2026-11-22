using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Tools;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class LogQueryTests
{
    private static LogEntry Entry(string level, string message, string? client = "c1") =>
        new(DateTimeOffset.UnixEpoch, level, message, client);

    private static LogQuery Query(
        string? level = null, string? contains = null, bool includeOther = false, string client = "c1") =>
        new(50, "memory", level, contains, includeOther, client);

    [Fact]
    public void HasTextFilterIsFalseWithoutFilters()
    {
        Assert.False(Query().HasTextFilter);
    }

    [Fact]
    public void HasTextFilterDetectsLevel()
    {
        Assert.True(Query(level: "WARN").HasTextFilter);
    }

    [Fact]
    public void HasTextFilterDetectsContains()
    {
        Assert.True(Query(contains: "timeout").HasTextFilter);
    }

    [Fact]
    public void VisibleAllowsAnyClientWhenRequested()
    {
        Assert.True(Query(includeOther: true).Visible(Entry("INFO", "x", "other")));
    }

    [Fact]
    public void VisibleAllowsEntriesWithoutClient()
    {
        Assert.True(Query().Visible(Entry("INFO", "x", client: null)));
    }

    [Fact]
    public void VisibleAllowsCurrentClient()
    {
        Assert.True(Query().Visible(Entry("INFO", "x", "c1")));
    }

    [Fact]
    public void VisibleRejectsOtherClient()
    {
        Assert.False(Query().Visible(Entry("INFO", "x", "c2")));
    }

    [Fact]
    public void MatchesLevelCaseInsensitively()
    {
        Assert.True(Query(level: "warn").Matches(Entry("WARN", "x")));
    }

    [Fact]
    public void MatchesContainsCaseInsensitively()
    {
        Assert.True(Query(contains: "TIMEOUT").Matches(Entry("INFO", "request timeout")));
    }

    [Fact]
    public void MatchesRejectsDifferentLevel()
    {
        Assert.False(Query(level: "ERROR").Matches(Entry("INFO", "x")));
    }

    [Fact]
    public void MatchesIgnoresWhitespaceLevel()
    {
        Assert.True(Query(level: "   ").Matches(Entry("DEBUG", "x")));
    }
}
