using System.Text.Json.Nodes;
using VScreenPilot.Server.Infrastructure;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class PrivacyRedactorTests
{
    [Fact]
    public void RedactEmailsReplacesAddressAndCounts()
    {
        int count = 0;

        string result = PrivacyRedactor.RedactEmails("mail me at a@b.com", ref count);

        Assert.DoesNotContain("a@b.com", result);
        Assert.Equal(1, count);
    }

    [Fact]
    public void RedactEmailsCountsEachAddress()
    {
        int count = 0;

        PrivacyRedactor.RedactEmails("a@b.com and c@d.com", ref count);

        Assert.Equal(2, count);
    }

    [Fact]
    public void RedactEmailsLeavesPlainTextUntouched()
    {
        int count = 0;

        string result = PrivacyRedactor.RedactEmails("no address here", ref count);

        Assert.Equal("no address here", result);
        Assert.Equal(0, count);
    }

    [Fact]
    public void RedactJsonStringsRewritesObjectValue()
    {
        int count = 0;
        var node = new JsonObject { ["mail"] = "a@b.com" };

        PrivacyRedactor.RedactJsonStrings(node, ref count);

        Assert.DoesNotContain("a@b.com", node.ToJsonString());
        Assert.Equal(1, count);
    }

    [Fact]
    public void RedactJsonStringsRewritesArrayValue()
    {
        int count = 0;
        var node = new JsonArray("a@b.com", "plain");

        PrivacyRedactor.RedactJsonStrings(node, ref count);

        Assert.DoesNotContain("a@b.com", node.ToJsonString());
        Assert.Equal(1, count);
    }

    [Fact]
    public void RedactJsonStringsWalksNestedObjects()
    {
        int count = 0;
        var node = new JsonObject
        {
            ["outer"] = new JsonObject { ["inner"] = "a@b.com" },
        };

        PrivacyRedactor.RedactJsonStrings(node, ref count);

        Assert.DoesNotContain("a@b.com", node.ToJsonString());
        Assert.Equal(1, count);
    }

    [Fact]
    public void RedactJsonStringsIgnoresNonStringScalars()
    {
        int count = 0;
        var node = new JsonObject { ["count"] = 42, ["flag"] = true };

        PrivacyRedactor.RedactJsonStrings(node, ref count);

        Assert.Equal(0, count);
        Assert.Equal(42, node["count"]!.GetValue<int>());
    }

    [Fact]
    public void RedactJsonStringsAccumulatesCount()
    {
        int count = 0;
        var node = new JsonObject { ["a"] = "a@b.com", ["b"] = "c@d.com" };

        PrivacyRedactor.RedactJsonStrings(node, ref count);

        Assert.Equal(2, count);
    }
}
