using System.Text.Json.Nodes;
using VScreenPilot.Json;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class TreeViewSerializationTests
{
    [Fact]
    public void FindElementSingleKeepsCompactShapeThenRefAndMeta()
    {
        var view = new FindElementView(
            "N", "a1", "Button", new BoundsView(1, 2, 3, 4), false, null, true, "v",
            "e9", "contains", null, "auto", 0, 1, false, "s1");

        Assert.Equal(
            "{\"name\":\"N\",\"automation_id\":\"a1\",\"control_type\":\"Button\"," +
            "\"bounds\":{\"x\":1,\"y\":2,\"width\":3,\"height\":4},\"enabled\":false," +
            "\"focused\":true,\"value\":\"v\",\"ref\":\"e9\",\"name_match\":\"contains\"," +
            "\"role\":null,\"prefer\":\"auto\",\"index\":0,\"count\":1,\"truncated\":false," +
            "\"snapshot_id\":\"s1\"}",
            VspJson.ToText(view));
    }

    [Fact]
    public void FindElementsAllShapeMatchesLegacyFieldOrder()
    {
        var view = new FindElementsView(
            1, 3, true, "exact", "document", "largest", "s2",
            new[] { new FindElementItemView("N", "", "Edit", new BoundsView(0, 0, 1, 1), null, null, null, null, "e1") });

        Assert.Equal(
            "{\"count\":1,\"total\":3,\"truncated\":true,\"name_match\":\"exact\"," +
            "\"role\":\"document\",\"prefer\":\"largest\",\"snapshot_id\":\"s2\"," +
            "\"elements\":[{\"name\":\"N\",\"automation_id\":\"\",\"control_type\":\"Edit\"," +
            "\"bounds\":{\"x\":0,\"y\":0,\"width\":1,\"height\":1},\"ref\":\"e1\"}]}",
            VspJson.ToText(view));
    }

    [Fact]
    public void UiTreeEmitsOptionalFieldsOnlyWhenPresent()
    {
        var view = new UiTreeView(
            "r1", new JsonObject { ["ref"] = "r1" }, 1, 2, 0, false, true, 3, 2, 12.5, 1024,
            300, "s3", false, null, null, null, null, null);

        Assert.Equal(
            "{\"root_ref\":\"r1\",\"tree\":{\"ref\":\"r1\"},\"nodes\":1,\"visited\":2," +
            "\"pruned\":0,\"truncated\":false,\"interactive_only\":true,\"max_depth\":3," +
            "\"max_depth_reached\":2,\"elapsed_ms\":12.5,\"ref_capacity\":1024," +
            "\"max_nodes_effective\":300,\"snapshot_id\":\"s3\",\"new_snapshot\":false}",
            VspJson.ToText(view));
    }

    [Fact]
    public void MsaaTreeKeepsBackendAndTruncatedReason()
    {
        var view = new UiTreeView(
            "r2", new JsonObject(), 2, null, null, true, true, 1, null, null, 1024, 100,
            "s4", false, "msaa", null, "msaa_limit", null, null);

        Assert.Equal(
            "{\"root_ref\":\"r2\",\"tree\":{},\"nodes\":2,\"truncated\":true," +
            "\"interactive_only\":true,\"max_depth\":1,\"ref_capacity\":1024," +
            "\"max_nodes_effective\":100,\"snapshot_id\":\"s4\",\"new_snapshot\":false," +
            "\"backend\":\"msaa\",\"truncated_reason\":\"msaa_limit\"}",
            VspJson.ToText(view));
    }

    [Fact]
    public void FindElementObjectSerializesRuntimeType()
    {
        object view = new FindElementView(
            "N", "", "Button", new BoundsView(0, 0, 1, 1), null, null, null, null,
            "e1", "exact", null, "auto", 0, 1, false, "s1");

        string text = VspJson.ToText(view);

        Assert.StartsWith("{\"name\":\"N\"", text);
        Assert.Contains("\"ref\":\"e1\"", text);
    }
}
