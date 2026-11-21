using System.Text.Json.Nodes;
using VScreenPilot.Diagnostics;
using VScreenPilot.Platform.Logging;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests.Unit;

public sealed class ArtifactSummaryTests
{
    [Fact]
    public void DescribeReturnsRootAndRetentionOnly()
    {
        JsonObject summary = ArtifactSummary.Describe("unit-profile");

        string expectedRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VScreenPilot",
            "artifacts",
            "unit-profile");

        Assert.Equal(expectedRoot, summary["root"]!.GetValue<string>());
        Assert.InRange(summary["retention_days"]!.GetValue<int>(), 0, int.MaxValue);
        Assert.Equal(2, summary.Count);
    }

    [Fact]
    public void RootForMatchesArtifactLayout()
    {
        string root = ArtifactStore.RootFor("unit-profile");

        Assert.EndsWith(Path.Combine("VScreenPilot", "artifacts", "unit-profile"), root);
        Assert.Equal(ArtifactStore.RootFor("unit-profile"), root);
    }

    [Fact]
    public void RetentionEnvironmentNameIsStable()
    {
        Assert.Equal("VSP_ARTIFACT_RETENTION_DAYS", ArtifactStore.RetentionEnv);
    }
}

public sealed class LogSinkTests
{
    [Fact]
    public void ConfigureCreatesTraceFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"vsp-log-{Guid.NewGuid():N}.log");
        try
        {
            LogSink.Configure(path);
            LogSink.Debug("unit-check");
            System.Diagnostics.Trace.Flush();

            Assert.True(File.Exists(path), "LogSink 未创建 trace 文件");
            Assert.True(new FileInfo(path).Length > 0, "LogSink 未写入任何内容");
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // listener 仍持有文件句柄；临时文件交给系统清理。
            }
        }
    }
}
