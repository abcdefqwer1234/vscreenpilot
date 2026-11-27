using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class FrameStoreTests
{
    [Fact]
    public void ClientQuotaDoesNotEvictOtherClients()
    {
        var store = new FrameStore();
        var ids = new List<string>();

        for (int index = 0; index < 7; index++)
        {
            ids.Add(store.Store(new byte[4], 1, 1, index, 0, "c1"));
        }

        string other = store.Store(new byte[4], 1, 1, 9, 9, "c2");

        Assert.Null(store.FindById("c1", ids[0]));
        Assert.NotNull(store.FindById("c1", ids[6]));
        Assert.NotNull(store.FindById("c2", other));
    }

    [Fact]
    public void GlobalCapStillApplies()
    {
        var store = new FrameStore();
        var ids = new List<string>();

        for (int index = 0; index < 9; index++)
        {
            ids.Add(store.Store(new byte[4], 1, 1, index, 0, "c" + index % 8));
        }

        Assert.True(store.Snapshot().Count <= 8);
        Assert.Null(store.FindById("c0", ids[0]));
    }

    [Fact]
    public void LatestAndSnapshotAreClientScoped()
    {
        var store = new FrameStore();
        string first = store.Store(new byte[4], 1, 1, 10, 0, "c1");
        string second = store.Store(new byte[4], 1, 1, 20, 0, "c2");
        string third = store.Store(new byte[4], 1, 1, 30, 0, "c1");

        Assert.Equal(third, store.Latest("c1")?.Id);
        Assert.Equal(second, store.Latest("c2")?.Id);
        Assert.Equal([first, third], store.Snapshot("c1").Select(frame => frame.Id).ToArray());
        Assert.Equal([second], store.Snapshot("c2").Select(frame => frame.Id).ToArray());
        Assert.Null(store.Latest("c3"));
    }

    [Fact]
    public void PerClientByteQuotaDoesNotEvictOtherClients()
    {
        var store = new FrameStore();
        int frameBytes = 12 * 1024 * 1024;

        string other = store.Store(new byte[frameBytes], 1, 1, 0, 0, "c2");
        string first = store.Store(new byte[frameBytes], 1, 1, 1, 0, "c1");
        string second = store.Store(new byte[frameBytes], 1, 1, 2, 0, "c1");

        Assert.NotNull(store.FindById("c2", other));
        Assert.NotNull(store.FindById("c1", second));
        Assert.Null(store.FindById("c1", first));
    }

    [Fact]
    public void GlobalEvictionKeepsLatestFramePerClient()
    {
        var store = new FrameStore();

        for (int index = 0; index < 6; index++)
        {
            store.Store(new byte[16], 1, 1, index, 0, "c1");
            store.Store(new byte[16], 1, 1, index, 1, "c2");
        }

        Assert.NotNull(store.Latest("c1"));
        Assert.NotNull(store.Latest("c2"));
        Assert.True(store.Snapshot().Count <= 8);
    }
}
