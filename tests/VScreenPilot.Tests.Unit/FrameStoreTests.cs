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
}
