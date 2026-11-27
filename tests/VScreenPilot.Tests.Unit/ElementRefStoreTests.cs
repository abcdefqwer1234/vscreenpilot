using VScreenPilot.Ui;
using VScreenPilot.Uia;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ElementRefStoreTests
{
    private static UiaElementInfo Element(string name = "OK") => new(
        RuntimeId: "42;1",
        Name: name,
        AutomationId: "okButton",
        ControlType: 50000,
        ControlTypeName: "Button",
        Left: 10,
        Top: 10,
        Right: 80,
        Bottom: 30,
        Enabled: true,
        Offscreen: false,
        ProcessId: 4242,
        ClassName: "Button");

    [Fact]
    public void RefsSurviveRetainedGenerations()
    {
        var store = new ElementRefStore();
        const string client = "c1";

        store.BeginSnapshot(client);
        string reference = store.Register(client, Element(), new IntPtr(0x100));

        // 默认保留 3 代：第 3 代仍能解析，第 4 代才淘汰。
        store.BeginSnapshot(client);
        store.BeginSnapshot(client);

        Assert.NotNull(store.TryResolve(client, reference, out bool stale));
        Assert.False(stale);

        store.BeginSnapshot(client);

        Assert.Null(store.TryResolve(client, reference, out stale));
        Assert.True(stale);
    }

    [Fact]
    public void OlderButRetainedRefIsNotReportedAsStale()
    {
        var store = new ElementRefStore();
        const string client = "c2";

        store.BeginSnapshot(client);
        string reference = store.Register(client, Element("Name"), new IntPtr(0x200));
        store.BeginSnapshot(client);

        ElementRefEntry? entry = store.TryResolve(client, reference, out bool stale);

        Assert.NotNull(entry);
        Assert.False(stale);
        Assert.Equal("Name", entry.Name);
    }
}
