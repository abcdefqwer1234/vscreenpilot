using System.Text.Json.Nodes;
using VScreenPilot.Session;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ElementPresenterTests
{
    private static UiaElementInfo Info(
        bool enabled = true, bool password = false, string value = "", string text = "") =>
        new(
            RuntimeId: "42",
            Name: "Save",
            AutomationId: "save",
            ControlType: 50000,
            ControlTypeName: "Button",
            Left: 10,
            Top: 20,
            Right: 40,
            Bottom: 60,
            Enabled: enabled,
            Offscreen: false,
            ProcessId: 7,
            ClassName: "Button",
            Focusable: true,
            Focused: true,
            Password: password,
            Value: value,
            Text: text);

    [Fact]
    public void CompactViewOmitsRuntimeAndProcessIds()
    {
        ElementView view = ElementPresenter.ToView(Info(), compact: true);

        Assert.Null(view.RuntimeId);
        Assert.Null(view.ControlTypeId);
        Assert.Null(view.ProcessId);
    }

    [Fact]
    public void CompactViewKeepsDisabledFlag()
    {
        ElementView view = ElementPresenter.ToView(Info(enabled: false), compact: true);

        Assert.False(view.Enabled);
    }

    [Fact]
    public void FullViewCarriesIdentifiers()
    {
        ElementView view = ElementPresenter.ToView(Info(), compact: false);

        Assert.Equal("42", view.RuntimeId);
        Assert.Equal(50000, view.ControlTypeId);
        Assert.Equal(7, view.ProcessId);
        Assert.Equal("Button", view.ClassName);
    }

    [Fact]
    public void CompactValueIsTruncatedTo200Chars()
    {
        ElementView view = ElementPresenter.ToView(
            Info(value: new string('x', 300)), compact: true);

        Assert.Equal(200, view.Value!.Length);
    }

    [Fact]
    public void PasswordValueIsHidden()
    {
        ElementView view = ElementPresenter.ToView(
            Info(password: true, value: "secret", text: "secret"), compact: false);

        Assert.Null(view.Value);
        Assert.Null(view.Text);
    }

    [Fact]
    public void CompactJsonOmitsEnabledWhenTrue()
    {
        JsonObject json = ElementPresenter.ToCompactJson(Info());

        Assert.False(json.ContainsKey("enabled"));
        Assert.True(json.ContainsKey("bounds"));
    }

    [Fact]
    public void CompactJsonAddsDisabledWhenFalse()
    {
        JsonObject json = ElementPresenter.ToCompactJson(Info(enabled: false));

        Assert.False(json["enabled"]!.GetValue<bool>());
    }
}
