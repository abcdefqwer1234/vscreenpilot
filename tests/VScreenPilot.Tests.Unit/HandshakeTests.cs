using VScreenPilot.Ipc;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ClientHelloParsingTests
{
    private const string ValidHello =
        "{\"vsp\":1,\"client\":\"tester\",\"cwd\":\"C:\\\\work\",\"version\":\"1.2.3\"}";

    [Fact]
    public void ParseAcceptsValidHello()
    {
        ClientHello? hello = Handshake.ParseClientHello(ValidHello);

        Assert.NotNull(hello);
        Assert.Equal("tester", hello!.Client);
        Assert.Equal("1.2.3", hello.Version);
    }

    [Fact]
    public void ParseReturnsNullForBlankLine()
    {
        Assert.Null(Handshake.ParseClientHello("   "));
    }

    [Fact]
    public void ParseReturnsNullWithoutProtocol()
    {
        Assert.Null(Handshake.ParseClientHello("{\"cwd\":\"C:\\\\work\"}"));
    }

    [Fact]
    public void ParseRejectsOtherProtocolVersion()
    {
        Assert.Null(Handshake.ParseClientHello(
            "{\"vsp\":2,\"cwd\":\"C:\\\\work\"}"));
    }

    [Fact]
    public void ParseReturnsNullWithoutCwd()
    {
        Assert.Null(Handshake.ParseClientHello("{\"vsp\":1}"));
    }

    [Fact]
    public void ParseRejectsRelativeCwd()
    {
        Assert.Null(Handshake.ParseClientHello("{\"vsp\":1,\"cwd\":\"relative\"}"));
    }

    [Fact]
    public void RootsDefaultToCwd()
    {
        ClientHello? hello = Handshake.ParseClientHello(ValidHello);

        Assert.NotNull(hello);
        Assert.Single(hello!.Roots);
        Assert.Equal(hello.Cwd, hello.Roots[0]);
    }

    [Fact]
    public void CapabilitiesAreRead()
    {
        ClientHello? hello = Handshake.ParseClientHello(
            "{\"vsp\":1,\"cwd\":\"C:\\\\work\",\"proxy_pid\":42," +
            "\"capabilities\":{\"allow_script\":true,\"allow_unsafe_script\":true}}");

        Assert.NotNull(hello);
        Assert.Equal(42, hello!.ProxyPid);
        Assert.True(hello.AllowScript);
        Assert.True(hello.AllowUnsafeScript);
    }
}
