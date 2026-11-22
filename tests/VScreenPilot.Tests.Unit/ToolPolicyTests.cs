using VScreenPilot.Mcp;
using Xunit;

using VScreenPilot.Tools;

namespace VScreenPilot.Tests;

/// <summary>
/// 工具策略单点化的回归测试：调度行为只由 ToolPolicy 决定，
/// 不允许再出现“哪些工具会写/要租约”的旁路清单。
/// </summary>
public sealed class ToolPolicyTests
{
    private static ToolRegistry BuildCatalog()
    {
        // trace=false：只构建目录，不读会话、不写 trace。
        return new McpTransport(null!, null!, trace: false).BuildTools();
    }

    [Fact]
    public void CatalogHasExactly22UniqueTools()
    {
        ToolRegistry catalog = BuildCatalog();

        Assert.Equal(22, catalog.Count);
        Assert.Equal(22, catalog.All.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void EveryToolHasSchemasAndHandler()
    {
        foreach (ToolDescriptor tool in BuildCatalog().All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Title));
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
            Assert.NotNull(tool.InputSchema);
            Assert.NotNull(tool.OutputSchema);
            Assert.NotNull(tool.Handler);
            Assert.True(tool.TimeoutMs > 0);
        }
    }

    [Fact]
    public void DestructiveToolsAreAlwaysMutating()
    {
        foreach (ToolDescriptor tool in BuildCatalog().All)
        {
            if (tool.Destructive)
            {
                Assert.True(tool.Policy.Mutates, $"{tool.Name} 标记 destructive 但策略未声明 Mutates");
            }
        }
    }

    [Fact]
    public void SessionWriteToolsBypassAndUseShortWriteTimeout()
    {
        foreach (ToolDescriptor tool in BuildCatalog().All)
        {
            if (!tool.Policy.SessionWrite)
            {
                continue;
            }

            Assert.True(tool.Policy.Mutates, $"{tool.Name} 写会话但不是写操作");
            Assert.True(tool.Policy.BypassWriteLock, $"{tool.Name} 写会话必须能旁路写锁");
            Assert.True(tool.Policy.WriteTimeoutMs < 30000, $"{tool.Name} 写会话应使用短超时");
        }
    }

    [Fact]
    public void ReleaseEnvironmentPolicyIsExplicit()
    {
        ToolDescriptor release = BuildCatalog().All.Single(item => item.Name == "release_environment");
        Assert.True(release.Policy.Mutates);
        Assert.True(release.Policy.SessionWrite);
        Assert.Equal(6000, release.Policy.WriteTimeoutMs);
        Assert.Equal(ScreenLeaseMode.IfLiveApps, release.Policy.Lease);
    }

    [Fact]
    public void ReadOnlyObserversDoNotTakeScreenLease()
    {
        Dictionary<string, ToolDescriptor> tools = BuildCatalog().All.ToDictionary(i => i.Name);

        foreach (string name in new[] { "get_log", "find_element", "get_ui_tree", "get_element_text" })
        {
            Assert.Equal(ScreenLeaseMode.None, tools[name].Policy.Lease);
        }

        Assert.False(tools["get_log"].Policy.SessionRead);
        Assert.False(tools["wait_for"].Policy.SessionRead);
        Assert.Equal(ScreenLeaseMode.WaitCondition, tools["wait_for"].Policy.Lease);
        Assert.Equal(ScreenLeaseMode.SnapshotOptions, tools["snapshot"].Policy.Lease);
        Assert.Equal(ScreenLeaseMode.EnsureOption, tools["get_environment"].Policy.Lease);
    }

    [Fact]
    public void RegistryRejectsNothingAndOverwritesByName()
    {
        var registry = new ToolRegistry();
        ToolDescriptor first = BuildCatalog().All.First();
        registry.Add(first);
        registry.Add(first with { Title = "changed" });

        Assert.Equal(1, registry.Count);
        Assert.True(registry.TryGet(first.Name, out ToolDescriptor? found));
        Assert.Equal("changed", found!.Title);
        Assert.False(registry.TryGet("no_such_tool", out _));
    }
}
