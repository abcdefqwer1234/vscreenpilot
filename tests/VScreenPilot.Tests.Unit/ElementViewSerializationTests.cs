using VScreenPilot.Json;
using VScreenPilot.Session;
using VScreenPilot.Uia;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ElementViewSerializationTests
{
    private static UiaElementInfo Info() => new(
        RuntimeId: "r1",
        Name: "N",
        AutomationId: "a1",
        ControlType: 50000,
        ControlTypeName: "Button",
        Left: 1,
        Top: 2,
        Right: 11,
        Bottom: 22,
        Enabled: true,
        Offscreen: false,
        ProcessId: 7,
        ClassName: "C",
        Focusable: true,
        Focused: true,
        Password: false,
        Value: "v",
        Text: "t");

    [Fact]
    public void FullElementViewMatchesLegacyFieldOrder()
    {
        Assert.Equal(
            "{\"runtime_id\":\"r1\",\"name\":\"N\",\"automation_id\":\"a1\",\"control_type\":\"Button\"," +
            "\"control_type_id\":50000,\"bounds\":{\"x\":1,\"y\":2,\"width\":10,\"height\":20}," +
            "\"enabled\":true,\"offscreen\":false,\"process_id\":7,\"class_name\":\"C\"," +
            "\"focusable\":true,\"focused\":true,\"value\":\"v\",\"text\":\"t\"}",
            VspJson.ToText(ElementPresenter.ToView(Info(), compact: false)));
    }

    [Fact]
    public void CompactElementViewMatchesLegacyOmissions()
    {
        Assert.Equal(
            "{\"name\":\"N\",\"automation_id\":\"a1\",\"control_type\":\"Button\"," +
            "\"bounds\":{\"x\":1,\"y\":2,\"width\":10,\"height\":20},\"focused\":true,\"value\":\"v\"}",
            VspJson.ToText(ElementPresenter.ToView(Info(), compact: true)));
    }

    [Fact]
    public void CompactElementViewEmitsDisabledFlag()
    {
        UiaElementInfo info = Info() with { Enabled = false };

        Assert.Contains("\"enabled\":false", VspJson.ToText(ElementPresenter.ToView(info, compact: true)));
    }
}
