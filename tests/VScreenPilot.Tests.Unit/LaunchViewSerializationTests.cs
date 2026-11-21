using VScreenPilot.Json;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class LaunchViewSerializationTests
{
    private static readonly string[] Warnings = { "w1", "w2" };

    [Fact]
    public void LaunchViewKeepsOptionalFieldsAndEmptyBounds()
    {
        var view = new LaunchOrPlaceView(
            Launched: true,
            NewInstance: true,
            Reused: false,
            Maximized: true,
            CloseOnRelease: true,
            Hwnd: 10,
            Pid: 20,
            Title: "T",
            DocumentReady: null,
            DocumentReadyReason: "not_modeled",
            EnsureDocument: true,
            Bounds: new OptionalBoundsView(null, null, null, null),
            Warnings: null,
            Profile: null,
            Hint: null,
            Dialogs: new[] { new LaunchDialogView(30, "D", "C") });

        Assert.Equal(
            "{\"launched\":true,\"new_instance\":true,\"reused\":false,\"maximized\":true," +
            "\"close_on_release\":true,\"hwnd\":10,\"pid\":20,\"title\":\"T\"," +
            "\"document_ready\":null,\"document_ready_reason\":\"not_modeled\",\"ensure_document\":true," +
            "\"bounds\":{},\"dialogs\":[{\"hwnd\":30,\"title\":\"D\",\"class\":\"C\"}]}",
            VspJson.ToText(view));
    }

    [Fact]
    public void LaunchViewEmitsWarningsProfileAndHintWhenPresent()
    {
        var view = new LaunchOrPlaceView(
            true, false, true, true, false, 1, 2, "T", true, "document_element", true,
            new OptionalBoundsView(0, 1, 2, 3),
            new[] { "w1", "w2" }, "profile", "hint",
            Array.Empty<LaunchDialogView>());

        Assert.Equal(
            "{\"launched\":true,\"new_instance\":false,\"reused\":true,\"maximized\":true," +
            "\"close_on_release\":false,\"hwnd\":1,\"pid\":2,\"title\":\"T\",\"document_ready\":true," +
            "\"document_ready_reason\":\"document_element\",\"ensure_document\":true," +
            "\"bounds\":{\"x\":0,\"y\":1,\"width\":2,\"height\":3}," +
            "\"warnings\":[\"w1\",\"w2\"],\"profile\":\"profile\",\"hint\":\"hint\",\"dialogs\":[]}",
            VspJson.ToText(view));
    }
}
