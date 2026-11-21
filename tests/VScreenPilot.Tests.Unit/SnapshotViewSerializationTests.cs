using System.Text.Json.Nodes;
using VScreenPilot.Json;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class SnapshotViewSerializationTests
{
    private static SnapshotView View() => new(
        SnapshotId: "s1",
        RootRef: "e1",
        Tree: new JsonObject { ["ref"] = "e1" },
        Text: "e1 Button",
        Nodes: 1,
        Visited: 1,
        Pruned: 0,
        Truncated: false,
        InteractiveOnly: true,
        MaxDepth: 4,
        MaxNodesEffective: 300,
        RefCapacity: 1024,
        ElapsedMs: 12.5,
        Window: new SnapshotWindowView(10, 20, "T", "a.exe"),
        PrivacyRedactions: null,
        EmptyRetry: null,
        EmptyReason: null,
        MaxNodesClamped: null,
        TruncatedReason: null,
        Dialogs: new JsonArray(),
        RootHwnd: null,
        RootIsDialog: null,
        Windows: new JsonArray(),
        Foreground: null,
        Annotations: null,
        Screenshot: null,
        ScreenshotPath: null,
        ScreenshotUri: null,
        ScreenshotResourceUri: null,
        ScreenshotError: null,
        ScreenshotPreview: null);

    [Fact]
    public void SnapshotEnvelopeOmitsOptionalFields()
    {
        Assert.Equal(
            "{\"snapshot_id\":\"s1\",\"root_ref\":\"e1\",\"tree\":{\"ref\":\"e1\"}," +
            "\"text\":\"e1 Button\",\"nodes\":1,\"visited\":1,\"pruned\":0,\"truncated\":false," +
            "\"interactive_only\":true,\"max_depth\":4,\"max_nodes_effective\":300," +
            "\"ref_capacity\":1024,\"elapsed_ms\":12.5," +
            "\"window\":{\"hwnd\":10,\"pid\":20,\"title\":\"T\",\"exe\":\"a.exe\"}," +
            "\"dialogs\":[],\"windows\":[],\"foreground\":null}",
            VspJson.ToText(View()));
    }

    [Fact]
    public void SnapshotWithAddsToolLayerFieldsAtTail()
    {
        SnapshotView view = View() with
        {
            ScreenshotPath = "C:/a.png",
            ScreenshotUri = "file:///a.png",
            ScreenshotResourceUri = "file:///a.png",
            ScreenshotPreview = new JsonObject { ["width"] = 10 },
        };

        string text = VspJson.ToText(view);

        Assert.EndsWith(
            "\"screenshot_path\":\"C:/a.png\",\"screenshot_uri\":\"file:///a.png\"," +
            "\"screenshot_resource_uri\":\"file:///a.png\",\"screenshot_preview\":{\"width\":10}}",
            text);
    }
}
