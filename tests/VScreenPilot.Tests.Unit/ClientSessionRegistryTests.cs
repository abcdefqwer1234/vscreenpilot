using VScreenPilot.Services;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>
/// 共享 daemon 下握手上下文不能被压缩成 5 字段副本：
/// 已登记的实例要复用（epoch 跨调用保持），未登记时才用握手 Context 或兜底构造。
/// </summary>
public sealed class ClientSessionRegistryTests
{
    [Fact]
    public void ResolveReturnsRegisteredContextAndKeepsEpoch()
    {
        var registry = new ClientSessionRegistry();
        var context = new ClientContext { Id = "c1", Cwd = @"C:\work", AllowScript = true };
        registry.Enter(context);
        var info = new ClientInfo("c1", @"C:\other", Array.Empty<string>(), "proxy");

        ClientContext resolved = registry.Resolve(info);

        Assert.Same(context, resolved);
        Interlocked.Increment(ref resolved.Epoch);
        Assert.Equal(1, registry.Resolve(info).Epoch);
    }

    [Fact]
    public void ResolveUsesHandshakeContextWithoutRegistering()
    {
        var registry = new ClientSessionRegistry();
        var context = new ClientContext { Id = "c2", AppAllowlist = ["C:\\tools"] };
        var info = new ClientInfo("c2", null, Array.Empty<string>(), "proxy") { Context = context };

        Assert.Same(context, registry.Resolve(info));
        Assert.Equal(0, registry.ActiveCount);
        Assert.Empty(registry.All);
    }

    [Fact]
    public void ResolveFallsBackToClientInfo()
    {
        var registry = new ClientSessionRegistry();
        var info = new ClientInfo("c9", @"C:\work", [@"C:\root"], "ctl", true);

        ClientContext first = registry.Resolve(info);
        ClientContext second = registry.Resolve(info);

        Assert.NotSame(first, second);
        Assert.Equal("c9", first.Id);
        Assert.Equal(@"C:\work", first.Cwd);
        Assert.Equal([@"C:\root"], first.Roots);
        Assert.True(first.IncludeAllApps);
        Assert.Equal(0, registry.ActiveCount);
    }

    [Fact]
    public void LeaveRemovesRegistration()
    {
        var registry = new ClientSessionRegistry();
        var context = new ClientContext { Id = "c3" };
        registry.Enter(context);
        Assert.Equal(1, registry.ActiveCount);

        registry.Leave("c3");

        Assert.Equal(0, registry.ActiveCount);
        Assert.NotSame(context, registry.Resolve(new ClientInfo("c3", null, Array.Empty<string>())));
    }
}
