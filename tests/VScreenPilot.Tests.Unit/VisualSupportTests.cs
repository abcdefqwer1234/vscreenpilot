using System.Text.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Session;
using Xunit;
using VScreenPilot.Errors;

namespace VScreenPilot.Tests;

public sealed class VisualSupportTests
{
    [Fact]
    public void ToHexUsesBgraOrder()
    {
        byte[] bgra = { 0x30, 0x20, 0x10, 0xFF };

        Assert.Equal("#102030", VisualSupport.ToHex(bgra, 0));
    }

    [Theory]
    [InlineData("#112233", 0x11, 0x22, 0x33)]
    [InlineData("112233", 0x11, 0x22, 0x33)]
    [InlineData("#FF112233", 0x11, 0x22, 0x33)]
    public void TryParseHexAcceptsCommonForms(string value, byte r, byte g, byte b)
    {
        Assert.True(VisualSupport.TryParseHex(value, out byte pr, out byte pg, out byte pb));
        Assert.Equal((r, g, b), (pr, pg, pb));
    }

    [Fact]
    public void TryParseHexRejectsShortInput()
    {
        Assert.False(VisualSupport.TryParseHex("#123", out _, out _, out _));
    }

    [Fact]
    public void ChangedRatioCountsPixelsBeyondTolerance()
    {
        byte[] baseline = { 0, 0, 0, 255, 10, 10, 10, 255 };
        byte[] current = { 20, 0, 0, 255, 10, 10, 10, 255 };

        Assert.Equal(0.5, VisualSupport.ChangedRatio(current, baseline, 8));
        Assert.Equal(0.0, VisualSupport.ChangedRatio(current, baseline, 20));
    }

    [Fact]
    public void ParseRegionReadsAllFourFields()
    {
        using JsonDocument document = JsonDocument.Parse(
            "{\"x\":1,\"y\":2,\"width\":3,\"height\":4}");

        Assert.Equal((1, 2, 3, 4), VisualSupport.ParseRegion(document.RootElement));
    }

    [Fact]
    public void ParseRegionRejectsMissingWidth()
    {
        using JsonDocument document = JsonDocument.Parse("{\"x\":1,\"y\":2,\"height\":4}");

        Assert.Throws<ToolException>(() => VisualSupport.ParseRegion(document.RootElement));
    }

    [Theory]
    [InlineData("Hello World", "world", true)]
    [InlineData("确定 取消", "取消", true)]
    [InlineData("abc", "zzz", false)]
    public void OcrTextMatchesIsCaseInsensitive(string haystack, string needle, bool expected)
    {
        Assert.Equal(expected, ScreenTextService.OcrTextMatches(haystack, needle));
    }
}
