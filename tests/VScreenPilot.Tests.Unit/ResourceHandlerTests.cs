using VScreenPilot.Mcp;
using VScreenPilot.Services;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>
/// MCP resources 回调不在工具调用的 AsyncLocal 作用域里：
/// 目录必须来自连接 id，而不是 Clients.CurrentId（否则共享 daemon 永远读 c0）。
/// </summary>
public sealed class ResourceHandlerTests
{
    [Fact]
    public void ArtifactDirectoryUsesConnectionClientId()
    {
        var artifacts = new FakeArtifacts();
        var handler = new ResourceHandler(artifacts, () => "c1");

        Assert.Equal(@"C:\art\c1", handler.ArtifactDirectory());
        Assert.Equal(["c1"], artifacts.Requested);
    }

    [Fact]
    public void ArtifactDirectoryDefaultsToC0()
    {
        var artifacts = new FakeArtifacts();
        var handler = new ResourceHandler(artifacts);

        Assert.Equal(@"C:\art\c0", handler.ArtifactDirectory());
        Assert.Equal(["c0"], artifacts.Requested);
    }

    private sealed class FakeArtifacts : IArtifactService
    {
        public List<string> Requested { get; } = new();

        public string Directory => @"C:\art\c0";

        public string DirectoryFor(string clientId)
        {
            Requested.Add(clientId);
            return @"C:\art\" + clientId;
        }

        public ArtifactOutput Write(byte[] png, string prefix, byte[]? preview = null) =>
            throw new NotSupportedException();
    }
}
