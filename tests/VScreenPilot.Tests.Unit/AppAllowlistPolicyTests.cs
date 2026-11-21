using VScreenPilot.Mcp;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>daemon 全局白名单与客户端白名单的交集规则。</summary>
public sealed class AppAllowlistPolicyTests
{
    private static readonly string[] GlobalApps = { @"C:\apps" };
    private static readonly string[] ClientApps = { @"C:\apps\client" };

    [Fact]
    public void NoRulesAllowsAnyPath()
    {
        var policy = new AppAllowlistPolicy(new FakeHost(), globalRoots: Array.Empty<string>());

        policy.Ensure(@"C:\anything\tool.exe");
    }

    [Fact]
    public void GlobalRuleAllowsPathUnderRoot()
    {
        var policy = new AppAllowlistPolicy(
            new FakeHost(), globalRoots: GlobalApps);

        policy.Ensure(@"C:\apps\sub\tool.exe");
        policy.Ensure(@"C:\APPS\SUB\TOOL.EXE");
    }

    [Fact]
    public void GlobalRuleRejectsPathOutsideRoot()
    {
        var policy = new AppAllowlistPolicy(
            new FakeHost(), globalRoots: GlobalApps);

        ToolException exception = Assert.Throws<ToolException>(
            () => policy.Ensure(@"C:\other\tool.exe"));

        Assert.Equal("E_APP_NOT_ALLOWED", exception.Code);
        Assert.Equal("global", exception.Details!["scope"]!.GetValue<string>());
        Assert.Equal(@"C:\other\tool.exe", exception.Details["path"]!.GetValue<string>());
        Assert.Single(exception.Details["allowed_roots"]!.AsArray());
    }

    [Fact]
    public void ClientRuleCanOnlyTightenGlobalRule()
    {
        var host = new FakeHost { ClientRoots = ClientApps };
        var policy = new AppAllowlistPolicy(host, globalRoots: GlobalApps);

        policy.Ensure(@"C:\apps\client\tool.exe");

        ToolException exception = Assert.Throws<ToolException>(
            () => policy.Ensure(@"C:\apps\server\tool.exe"));

        Assert.Equal("E_APP_NOT_ALLOWED", exception.Code);
        Assert.Equal("client", exception.Details!["scope"]!.GetValue<string>());
    }

    [Fact]
    public void EmptyPathSkipsCheckEvenWithRules()
    {
        var policy = new AppAllowlistPolicy(
            new FakeHost(), globalRoots: GlobalApps);

        policy.Ensure(null);
        policy.Ensure("   ");
    }

    [Fact]
    public void InvalidPathIsReportedAsInvalidArgument()
    {
        var host = new FakeHost
        {
            Resolver = _ => throw new ArgumentException("bad path"),
        };
        var policy = new AppAllowlistPolicy(host, globalRoots: GlobalApps);

        ToolException exception = Assert.Throws<ToolException>(
            () => policy.Ensure(@"C:\apps\tool.exe"));

        Assert.Equal("E_INVALID_ARG", exception.Code);
    }

    private sealed class FakeHost : IAppAllowlistHost
    {
        public string[] ClientRoots { get; init; } = Array.Empty<string>();

        public Func<string, string> Resolver { get; init; } = path => path;

        public string ResolvePath(string path) => Resolver(path);
    }
}
