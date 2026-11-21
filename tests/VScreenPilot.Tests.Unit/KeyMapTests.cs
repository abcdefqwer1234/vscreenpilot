using System.Text.Json.Nodes;
using VScreenPilot.Input;
using VScreenPilot.Session;
using VScreenPilot.Ui;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class KeyMapTests
{
    [Theory]
    [InlineData("Ctrl", 0x11)]
    [InlineData("Shift", 0x10)]
    [InlineData("Alt", 0x12)]
    [InlineData("Enter", 0x0D)]
    [InlineData("F12", 0x7B)]
    [InlineData("a", 0x41)]
    public void ResolvesNamedAndCharacterKeys(string token, int expected)
    {
        Assert.True(KeyMap.TryResolve(token, out ushort key, out _, out _, out bool altGr));
        Assert.Equal(expected, key);
        Assert.False(altGr);
    }

    [Fact]
    public void ShiftedCharacterReportsShift()
    {
        Assert.True(KeyMap.TryResolve("+", out _, out bool shift, out _, out _));
        Assert.True(shift);
    }

    [Fact]
    public void UnknownTokenFails()
    {
        Assert.False(KeyMap.TryResolve("NotAKey", out _, out _, out _, out _));
    }
}

public sealed class SnapshotFormatTests
{
    [Fact]
    public void RendersCompactTreeWithRefsAndFlags()
    {
        var child = new JsonObject
        {
            ["ref"] = "e2",
            ["control_type"] = "Button",
            ["name"] = "保存",
            ["enabled"] = false,
            ["value"] = "Ctrl+S",
        };

        var root = new JsonObject
        {
            ["ref"] = "e1",
            ["control_type"] = "Window",
            ["name"] = "文档1 - Word",
            ["children"] = new JsonArray(child),
        };

        string text = SnapshotTextFormatter.Render(root, includeValues: true);

        Assert.Contains("Window \"文档1 - Word\" [e1]", text);
        Assert.Contains("  - Button \"保存\"", text);
        Assert.Contains("[e2]", text);
        Assert.Contains("{disabled}", text);
        Assert.Contains("= \"Ctrl+S\"", text);
    }

    [Fact]
    public void EscapesNewlines()
    {
        var node = new JsonObject
        {
            ["control_type"] = "Text",
            ["name"] = "a\nb",
        };

        string text = SnapshotTextFormatter.Render(node, includeValues: false);
        Assert.Contains("a\\nb", text);
    }
}
