using System.Text.Json.Nodes;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>行式快照里 flags（{disabled}/{focused}）必须和 header 在同一行。</summary>
public sealed class SnapshotTextFormatterTests
{
    [Fact]
    public void FlagsStayOnTheHeaderLine()
    {
        var child = new JsonObject
        {
            ["ref"] = "e12",
            ["control_type"] = "Button",
            ["name"] = "保存",
            ["enabled"] = false,
        };

        var root = new JsonObject
        {
            ["ref"] = "e1",
            ["control_type"] = "Window",
            ["name"] = "文档1 - Word",
            ["children"] = new JsonArray(child),
        };

        string[] lines = SnapshotTextFormatter.Render(root, includeValues: false)
            .Split('\n');

        Assert.Equal("- Window \"文档1 - Word\" [e1]", lines[0].TrimEnd('\r'));
        Assert.Equal("  - Button \"保存\" [e12] {disabled}", lines[1].TrimEnd('\r'));
    }
}
