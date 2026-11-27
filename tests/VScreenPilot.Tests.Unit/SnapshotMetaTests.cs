using VScreenPilot.Ui;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class SnapshotMetaTests
{
    [Fact]
    public void BeginSnapshotRecordsRootAndAge()
    {
        var store = new ElementRefStore();
        string id = store.BeginSnapshot("c0", (IntPtr)0x1234);
        SnapshotMeta meta = Assert.IsType<SnapshotMeta>(store.GetSnapshotMeta("c0"));

        Assert.Equal(id, meta.SnapshotId);
        Assert.Equal(1, meta.Generation);
        Assert.Equal(0x1234, meta.RootHwnd.ToInt64());
        Assert.InRange(meta.AgeMs, 0, 60_000);

        store.SetSnapshotRoot("c0", (IntPtr)0x5678);
        Assert.Equal(0x5678, store.GetSnapshotMeta("c0")!.RootHwnd.ToInt64());
    }

    [Fact]
    public void EnsureSnapshotReusesGenerationButKeepsRoot()
    {
        var store = new ElementRefStore();
        string first = store.EnsureSnapshot("c0", (IntPtr)0x10);
        string second = store.EnsureSnapshot("c0", (IntPtr)0x20);

        Assert.Equal(first, second);
        Assert.Equal(0x10, store.GetSnapshotMeta("c0")!.RootHwnd.ToInt64());
    }

    [Fact]
    public void ClearingClientRemovesMeta()
    {
        var store = new ElementRefStore();
        store.EnsureSnapshot("c0", (IntPtr)0x10);
        store.ClearSnapshots("c0");

        Assert.Null(store.GetSnapshotMeta("c0"));
    }
}
