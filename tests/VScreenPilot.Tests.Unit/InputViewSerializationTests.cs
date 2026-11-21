using VScreenPilot.Json;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>输入类视图的 JSON 形状与旧实现逐字段一致（含可选字段与 null 语义）。</summary>
public sealed class InputViewSerializationTests
{
    [Fact]
    public void ClickViewKeepsFieldOrderAndOptionalFields()
    {
        var view = new ClickView(
            1, 2, "left", 1, "auto", "uia", 1.5, 2.5, true, false,
            new ObservedView(true, 0.25, 0.02, 16, "a", "b", 3.5, new RegionView(0, 0, 10, 10)),
            "detail", "hint");

        Assert.Equal(
            "{\"x\":1,\"y\":2,\"button\":\"left\",\"clicks\":1,\"mode\":\"auto\",\"method\":\"uia\"," +
            "\"inject_ms\":1.5,\"duration_ms\":2.5,\"verified\":true,\"keep_foreground\":false," +
            "\"observed\":{\"changed\":true,\"ratio\":0.25,\"threshold\":0.02,\"grid\":16," +
            "\"before\":\"a\",\"after\":\"b\",\"ms\":3.5,\"region\":{\"x\":0,\"y\":0,\"width\":10,\"height\":10}}," +
            "\"detail\":\"detail\",\"hint\":\"hint\"}",
            VspJson.ToText(view));
    }

    [Fact]
    public void ClickViewOmitsNullObservedAndHint()
    {
        var view = new ClickView(1, 2, "left", 1, "message", "message", 0, 0, false, true, null, null, null);

        Assert.Equal(
            "{\"x\":1,\"y\":2,\"button\":\"left\",\"clicks\":1,\"mode\":\"message\"," +
            "\"method\":\"message\",\"inject_ms\":0,\"duration_ms\":0,\"verified\":false," +
            "\"keep_foreground\":true}",
            VspJson.ToText(view));
    }

    [Fact]
    public void TypeTextViewKeepsNullKeys()
    {
        var view = new TypeTextView(3, 3, 10, 1, 32, true, true, "sendinput+verify", "keys", 1.2, 4.5, null, null);

        Assert.Equal(
            "{\"typed\":3,\"length\":3,\"target_pid\":10,\"chunks\":1,\"chunk_chars\":32," +
            "\"text_supported\":true,\"verified\":true,\"method\":\"sendinput+verify\",\"channel\":\"keys\"," +
            "\"inject_ms\":1.2,\"duration_ms\":4.5,\"observed_length\":null,\"warning\":null}",
            VspJson.ToText(view));
    }

    [Fact]
    public void PressKeysViewOmitsObservedWhenNull()
    {
        var view = new PressKeysView("Ctrl+S", 1, 10, 0.5, 2.5, true, null, null);

        Assert.Equal(
            "{\"keys\":\"Ctrl+S\",\"count\":1,\"target_pid\":10,\"inject_ms\":0.5," +
            "\"duration_ms\":2.5,\"keep_foreground\":true}",
            VspJson.ToText(view));
    }

    [Fact]
    public void GestureViewSerializesDrag()
    {
        var view = new GestureView(
            "drag", new PointView(1, 2), new PointView(3, 4), null, null, "left",
            "input", "ok", true, 1.5, 2.5, "hint", null);

        Assert.Equal(
            "{\"type\":\"drag\",\"from\":{\"x\":1,\"y\":2},\"to\":{\"x\":3,\"y\":4}," +
            "\"button\":\"left\",\"method\":\"input\",\"detail\":\"ok\",\"verified\":true," +
            "\"inject_ms\":1.5,\"duration_ms\":2.5,\"hint\":\"hint\"}",
            VspJson.ToText(view));
    }
}
