using VScreenPilot.Apps;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class TrackedAppRegistryTests
{
    private static TrackedApp App(int pid, string owner = "c0") =>
        new() { Pid = pid, OwnerClientId = owner, Title = $"app-{pid}" };

    [Fact]
    public void SnapshotIsIndependentFromLaterMutations()
    {
        var registry = new TrackedAppRegistry(new object());
        registry.Add(App(1));
        registry.Add(App(2));

        IReadOnlyList<TrackedApp> snapshot = registry.Snapshot();
        registry.Add(App(3));

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(3, registry.Count);
    }

    [Fact]
    public void QueriesAndRemovalUseTheSharedGate()
    {
        var gate = new object();
        var registry = new TrackedAppRegistry(gate);

        // 调用方持有同一把锁时，注册表不能自我死锁（Monitor 可重入）。
        lock (gate)
        {
            registry.Add(App(10));
            registry.Add(App(11, "c1"));

            Assert.True(registry.Any(item => item.Pid == 10));
            Assert.Equal("c1", registry.FirstOrDefault(item => item.Pid == 11)!.OwnerClientId);
            Assert.Single(registry.Where(item => item.OwnerClientId == "c0"));
            Assert.Equal(11, registry[1].Pid);
            Assert.Equal(2, registry.RemoveAll(item => item.Pid > 9));
            Assert.Equal(0, registry.Count);
        }

        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void RemoveAllTracksIndexesAndOrder()
    {
        var registry = new TrackedAppRegistry(new object());
        registry.Add(App(1));
        registry.Add(App(2));
        registry.Add(App(3));

        Assert.Equal(1, registry.RemoveAll(item => item.Pid == 2));
        Assert.Collection(
            registry.Snapshot(),
            item => Assert.Equal(1, item.Pid),
            item => Assert.Equal(3, item.Pid));

        registry.RemoveAt(0);
        Assert.Equal(3, registry.Snapshot()[0].Pid);
        Assert.Equal(3, registry.LastOrDefault(item => item.Pid == 3)!.Pid);
    }
}
