using System.Text;
using VScreenPilot.Infrastructure;
using VScreenPilot.Ipc;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class HandshakeTests
{
    [Fact]
    public void RejectsMissingOrRelativeCwd()
    {
        Assert.Null(Handshake.ParseClientHello("""{"vsp":1,"client":"x"}"""));
        Assert.Null(Handshake.ParseClientHello("""{"vsp":1,"client":"x","cwd":"."}"""));
    }

    [Fact]
    public void ParsesProxyIdentityAndNormalizesRoots()
    {
        string cwd = Path.Combine(Path.GetTempPath(), "vsp-handshake-" + Guid.NewGuid().ToString("N"));
        string payload = $$"""
            {"vsp":1,"client":"mcp","proxy_pid":123,"version":"9.9.9","cwd":{{System.Text.Json.JsonSerializer.Serialize(cwd)}},"roots":["."]}
            """;

        ClientHello? hello = Handshake.ParseClientHello(payload);

        Assert.NotNull(hello);
        Assert.Equal(123, hello.ProxyPid);
        Assert.Equal("9.9.9", hello.Version);
        Assert.Equal(Path.GetFullPath(cwd), hello.Cwd);
        Assert.Equal(Path.GetFullPath(cwd), hello.Roots[0]);
    }
}

public sealed class LogEntryParseTests
{
    [Fact]
    public void ParsesClientPrefixAndKeepsLegacyLines()
    {
        LogEntry? withClient = LogRing.Parse(
            "2026-11-18T12:00:00.0000000+08:00 [INFO] [client=c1] hello");
        Assert.NotNull(withClient);
        Assert.Equal("c1", withClient.ClientId);
        Assert.Equal("hello", withClient.Message);

        LogEntry? legacy = LogRing.Parse("2026-11-18T12:00:00.0000000+08:00 [WARN] old line");
        Assert.NotNull(legacy);
        Assert.Null(legacy.ClientId);
        Assert.Equal("old line", legacy.Message);
    }
}

[CollectionDefinition("ProfileEnvironment", DisableParallelization = true)]
public sealed class ProfileEnvironmentSet
{
}

[Collection("ProfileEnvironment")]
public sealed class MessageBudgetTests
{
    [Fact]
    public void BudgetIsSharedAndReleasedOnNewline()
    {
        var budget = new MessageBudget(4);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abc\nd\n"));
        using var limiter = new MessageLimiter(stream, budget);
        byte[] buffer = new byte[8];
        int read = limiter.Read(buffer, 0, buffer.Length);
        Assert.Equal(6, read);
        Assert.Equal(0, budget.InUse);
    }

    [Fact]
    public void OversizedLineClosesAtBudget()
    {
        var budget = new MessageBudget(4);
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("abcdef"));
        using var limiter = new MessageLimiter(stream, budget);

        Assert.Throws<IOException>(() =>
        {
            byte[] buffer = new byte[8];
            _ = limiter.Read(buffer, 0, buffer.Length);
        });
    }
}

public sealed class ProfileLengthTests
{
    [Fact]
    public void LongProfileIsCappedForPipeAndStateNames()
    {
        string raw = new string('a', 80);
        Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, raw);

        try
        {
            string profile = Profile.Resolve();
            Assert.True(profile.Length <= 32, profile);
            Assert.Equal(profile, Profile.Resolve());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, null);
        }
    }
}
