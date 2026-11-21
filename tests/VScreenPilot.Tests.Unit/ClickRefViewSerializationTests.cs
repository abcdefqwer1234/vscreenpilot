using VScreenPilot.Json;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ClickRefViewSerializationTests
{
    [Fact]
    public void UiaBranchOmitsCoordinateFields()
    {
        var view = new ClickRefView(
            true, "e1", "uia", null, null, null, null, null, null, "invoke",
            null, null, null, null, false,
            new ElementObservedView(true, true, true, false, null),
            new OptionalBoundsView(1, 2, 3, 4),
            null, null);

        Assert.Equal(
            "{\"clicked\":true,\"ref\":\"e1\",\"channel\":\"uia\",\"pattern\":\"invoke\"," +
            "\"refreshed\":false,\"observed\":{\"present\":true,\"enabled\":true," +
            "\"focused\":true,\"offscreen\":false},\"bounds\":{\"x\":1,\"y\":2,\"width\":3,\"height\":4}}",
            VspJson.ToText(view));
    }

    [Fact]
    public void CoordinateBranchCarriesClickFields()
    {
        var view = new ClickRefView(
            true, "e2", "message_click", 10, 20, "left", 1, "auto", "message_click",
            null, 1.5, 2.5, true, false, true,
            new ElementObservedView(true, null, null, null, null),
            null, "detail", null);

        Assert.Equal(
            "{\"clicked\":true,\"ref\":\"e2\",\"channel\":\"message_click\",\"x\":10,\"y\":20," +
            "\"button\":\"left\",\"clicks\":1,\"mode\":\"auto\",\"method\":\"message_click\"," +
            "\"inject_ms\":1.5,\"duration_ms\":2.5,\"verified\":true,\"keep_foreground\":false," +
            "\"refreshed\":true,\"observed\":{\"present\":true},\"detail\":\"detail\"}",
            VspJson.ToText(view));
    }

    [Fact]
    public void MsaaBranchSerializesEmptyBoundsAsObject()
    {
        var view = new ClickRefView(
            true, "e3", "msaa", null, null, null, null, null, null, null,
            null, null, null, null, false,
            new ElementObservedView(true, null, null, null, null),
            new OptionalBoundsView(null, null, null, null),
            null, null);

        Assert.Equal(
            "{\"clicked\":true,\"ref\":\"e3\",\"channel\":\"msaa\",\"refreshed\":false," +
            "\"observed\":{\"present\":true},\"bounds\":{}}",
            VspJson.ToText(view));
    }
}
