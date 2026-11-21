using System.Text.Json.Nodes;
using VScreenPilot.Json;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class VerifyViewSerializationTests
{
    [Fact]
    public void AssertColorOmitsChangedFields()
    {
        var view = new AssertRegionView(
            true, 0.95, 0.95, 0.9, "coverage 是请求阈值；actual_coverage/matched_ratio 是实际覆盖率",
            10, "#112233", "#112234", new SizeView(20, 10),
            null, null, null, null, null);

        Assert.Equal(
            "{\"passed\":true,\"matched_ratio\":0.95,\"actual_coverage\":0.95,\"coverage\":0.9," +
            "\"coverage_semantics\":\"coverage 是请求阈值；actual_coverage/matched_ratio 是实际覆盖率\"," +
            "\"tolerance\":10,\"expected\":\"#112233\",\"average\":\"#112234\"," +
            "\"region\":{\"width\":20,\"height\":10}}",
            VspJson.ToText(view));
    }

    [Fact]
    public void AssertChangedOmitsColorFields()
    {
        var view = new AssertRegionView(
            false, null, null, null, null, null, null, null, null,
            0.004, 0.01, true, "f1", new BoundsView(1, 2, 3, 4));

        Assert.Equal(
            "{\"passed\":false,\"changed_ratio\":0.004,\"threshold\":0.01," +
            "\"expect_changed\":true,\"baseline_frame_id\":\"f1\"," +
            "\"baseline\":{\"x\":1,\"y\":2,\"width\":3,\"height\":4}}",
            VspJson.ToText(view));
    }

    [Fact]
    public void WaitForViewKeepsPolymorphicLastPayload()
    {
        var last = new JsonObject { ["satisfied"] = true, ["actual"] = "#000000" };

        Assert.Equal(
            "{\"satisfied\":true,\"elapsed_ms\":120,\"last\":{\"satisfied\":true,\"actual\":\"#000000\"}}",
            VspJson.ToText(new WaitForView(true, 120, last)));
    }

    [Fact]
    public void PixelMatchSerializesPartialRegion()
    {
        var view = new MatchVisualView(
            "pixel", 0.5, new PointView(10, 20), "#AABBCC", new int[] { 0xCC, 0xBB, 0xAA },
            new OptionalBoundsView(1, 2, null, null),
            null, null, null, null, null, null, null, null, null, null, null, null);

        Assert.Equal(
            "{\"mode\":\"pixel\",\"elapsed_ms\":0.5,\"point\":{\"x\":10,\"y\":20}," +
            "\"color\":\"#AABBCC\",\"rgb\":[204,187,170],\"region\":{\"x\":1,\"y\":2}}",
            VspJson.ToText(view));
    }

    [Fact]
    public void TemplateMatchCarriesTemplateSizeAndScores()
    {
        var match = new VisualMatchView(
            1, 2, 3, 4, new PointView(2, 4), null, null, 0.91, 0.91, 1.25);

        var view = new MatchVisualView(
            "template", 3, null, null, null,
            new OptionalBoundsView(0, 0, 100, 100),
            null, null, null, null, null, null, 1, new[] { match }, 0.88,
            new TemplateSizeView(5, 6), false, 1.0);

        Assert.Equal(
            "{\"mode\":\"template\",\"elapsed_ms\":3,\"region\":{\"x\":0,\"y\":0,\"width\":100,\"height\":100}," +
            "\"count\":1,\"matches\":[{\"x\":1,\"y\":2,\"width\":3,\"height\":4," +
            "\"center\":{\"x\":2,\"y\":4},\"score\":0.91,\"confidence\":0.91,\"scale\":1.25}]," +
            "\"threshold\":0.88,\"template\":{\"width\":5,\"height\":6},\"ambiguous\":false," +
            "\"score_margin\":1}",
            VspJson.ToText(view));
    }
}
