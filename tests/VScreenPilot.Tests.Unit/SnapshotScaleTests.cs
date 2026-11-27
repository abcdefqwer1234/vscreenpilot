using System.Text.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Services;
using VScreenPilot.Session;
using VScreenPilot.Tools;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class SnapshotScaleTests
{
    [Fact]
    public void CaptureScreenAcceptsFourTimesScale()
    {
        var snapshot = new FakeSnapshotService();
        var tools = new SnapshotTools(snapshot, new FakeArtifactService());
        using JsonDocument document = JsonDocument.Parse(
            """{"region":{"x":0,"y":0,"width":200,"height":100},"scale":4}""");

        ToolResponse response = tools.CaptureScreen(document.RootElement);

        Assert.False(response.IsError);
        Assert.NotNull(snapshot.LastCapture);
        Assert.Equal(4.0, snapshot.LastCapture!.Scale);
    }

    [Fact]
    public void SnapshotAcceptsFourTimesScale()
    {
        var snapshot = new FakeSnapshotService();
        var tools = new SnapshotTools(snapshot, new FakeArtifactService());
        using JsonDocument document = JsonDocument.Parse("""{"scale":4}""");

        ToolResponse response = tools.Snapshot(document.RootElement);

        Assert.False(response.IsError);
        Assert.Equal(4.0, snapshot.LastSnapshotScale);
    }

    private sealed class FakeSnapshotService : ISnapshotService
    {
        public CaptureRequest? LastCapture { get; private set; }

        public double? LastSnapshotScale { get; private set; }

        public SnapshotOutput Snapshot(SnapshotRequest request)
        {
            LastSnapshotScale = request.Scale;
            return new SnapshotOutput(
                new SnapshotView(
                    SnapshotId: "s1",
                    RootRef: "e1",
                    Tree: new System.Text.Json.Nodes.JsonObject(),
                    Text: string.Empty,
                    Nodes: 0,
                    Visited: 0,
                    Pruned: 0,
                    Truncated: false,
                    InteractiveOnly: true,
                    MaxDepth: 1,
                    MaxNodesEffective: 1,
                    RefCapacity: 1,
                    ElapsedMs: 0,
                    Window: new SnapshotWindowView(1, 1, string.Empty, null),
                    PrivacyRedactions: null,
                    EmptyRetry: null,
                    EmptyReason: null,
                    MaxNodesClamped: null,
                    TruncatedReason: null,
                    Dialogs: null,
                    RootHwnd: null,
                    RootIsDialog: null,
                    Windows: null,
                    Foreground: null,
                    Annotations: null,
                    Screenshot: null,
                    ScreenshotPath: null,
                    ScreenshotUri: null,
                    ScreenshotResourceUri: null,
                    ScreenshotError: null,
                    ScreenshotPreview: null),
                null,
                null,
                null);
        }

        public CaptureOutput Capture(CaptureRequest request)
        {
            LastCapture = request;
            return new CaptureOutput(
                new byte[] { 1, 2, 3 },
                new CaptureView(
                    "f1", 0, 0, 800, 400, 200, 100, 4.0, 3, 1, 1,
                    "gdi", false, new BaselineView(0, 0, 200, 100), null),
                null,
                null);
        }
    }

    private sealed class FakeArtifactService : IArtifactService
    {
        public string Directory => Path.GetTempPath();

        public string DirectoryFor(string clientId) => Directory;

        public ArtifactOutput Write(byte[] png, string prefix, byte[]? preview = null) =>
            new(Path.Combine(Directory, prefix + ".png"), "vsp://artifacts/test.png");
    }
}
