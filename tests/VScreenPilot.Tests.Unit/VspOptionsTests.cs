using VScreenPilot.Options;
using Xunit;

namespace VScreenPilot.Tests.Unit;

/// <summary>
/// 环境变量的默认值与钳制范围属于外特性：任何一项变化都会改变部署行为。
/// 每个用例自己设置/恢复环境变量，避免相互污染。
/// </summary>
public sealed class VspOptionsTests
{
    private static T WithEnv<T>(string name, string? value, Func<T> action)
    {
        string? previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);

        try
        {
            return action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void DisplaySettingsClampToDocumentedRanges()
    {
        Assert.Equal(800, WithEnv("VSP_DISPLAY_WIDTH", "100", () => VspOptions.FromEnvironment().DisplayWidth));
        Assert.Equal(3840, WithEnv("VSP_DISPLAY_WIDTH", "9999", () => VspOptions.FromEnvironment().DisplayWidth));
        Assert.Equal(600, WithEnv("VSP_DISPLAY_HEIGHT", "-1", () => VspOptions.FromEnvironment().DisplayHeight));
        Assert.Equal(2160, WithEnv("VSP_DISPLAY_HEIGHT", "4000", () => VspOptions.FromEnvironment().DisplayHeight));
        Assert.Equal(30, WithEnv("VSP_DISPLAY_REFRESH", "1", () => VspOptions.FromEnvironment().DisplayRefresh));
        Assert.Equal(240, WithEnv("VSP_DISPLAY_REFRESH", "1000", () => VspOptions.FromEnvironment().DisplayRefresh));
    }

    [Fact]
    public void DisplaySettingsFallBackOnInvalidValues()
    {
        VspOptions options = WithEnv("VSP_DISPLAY_WIDTH", "abc", () => VspOptions.FromEnvironment());
        Assert.Equal(1280, options.DisplayWidth);

        VspOptions missing = WithEnv("VSP_DISPLAY_WIDTH", null, () => VspOptions.FromEnvironment());
        Assert.Equal(1280, missing.DisplayWidth);
    }

    [Fact]
    public void LeaseAndBufferRangesMatchBaseline()
    {
        Assert.Equal(1_000, WithEnv("VSP_SCREEN_LEASE_IDLE_MS", "1", () => VspOptions.FromEnvironment().ScreenLeaseIdleMs));
        Assert.Equal(600_000, WithEnv("VSP_SCREEN_LEASE_IDLE_MS", "9999999", () => VspOptions.FromEnvironment().ScreenLeaseIdleMs));
        Assert.Equal(120_000, WithEnv("VSP_SCREEN_LEASE_WAIT_MS", "9999999", () => VspOptions.FromEnvironment().ScreenLeaseWaitMs));
        Assert.Equal(8L * 1024 * 1024, WithEnv("VSP_TOTAL_BUFFER_MB", "1", () => VspOptions.FromEnvironment().TotalBufferBytes));
        Assert.Equal(512L * 1024 * 1024, WithEnv("VSP_TOTAL_BUFFER_MB", "9999", () => VspOptions.FromEnvironment().TotalBufferBytes));
        Assert.Equal(1L * 1024 * 1024, WithEnv("VSP_MAX_MESSAGE_MB", "0", () => VspOptions.FromEnvironment().MaxMessageBytes));
        Assert.Equal(64L * 1024 * 1024, WithEnv("VSP_MAX_MESSAGE_MB", "9999", () => VspOptions.FromEnvironment().MaxMessageBytes));
    }

    [Fact]
    public void ClientLimitAndRetentionDefaults()
    {
        Assert.Equal(16, WithEnv("VSP_CLIENT_LIMIT", null, () => VspOptions.FromEnvironment().ClientLimit));
        Assert.Equal(1, WithEnv("VSP_CLIENT_LIMIT", "0", () => VspOptions.FromEnvironment().ClientLimit));
        Assert.Equal(64, WithEnv("VSP_CLIENT_LIMIT", "999", () => VspOptions.FromEnvironment().ClientLimit));
        Assert.Equal(7, WithEnv("VSP_ARTIFACT_RETENTION_DAYS", "abc", () => VspOptions.FromEnvironment().ArtifactRetentionDays));
        Assert.Equal(0, WithEnv("VSP_ARTIFACT_RETENTION_DAYS", "0", () => VspOptions.FromEnvironment().ArtifactRetentionDays));
    }

    [Fact]
    public void TruthyFlagsKeepStrictAndLooseSemantics()
    {
        Assert.True(WithEnv("VSP_ALLOW_SCRIPT", "YES", () => VspOptions.FromEnvironment().AllowScript));
        Assert.False(WithEnv("VSP_ALLOW_SCRIPT", "on", () => VspOptions.FromEnvironment().AllowScript));
        Assert.True(WithEnv("VSP_ALLOW_UNSAFE_SCRIPT", " on ", () => VspOptions.FromEnvironment().AllowUnsafeScript));
        Assert.True(WithEnv("VSP_INPUT_AUTOFALLBACK", "true", () => VspOptions.FromEnvironment().InputAutoFallback));
        Assert.False(WithEnv("VSP_NO_WARMUP", "true", () => VspOptions.FromEnvironment().NoWarmup));
        Assert.True(WithEnv("VSP_NO_WARMUP", "1", () => VspOptions.FromEnvironment().NoWarmup));
    }

    [Fact]
    public void AllowlistAndProfileAreParsed()
    {
        VspOptions options = WithEnv(
            "VSP_APP_ALLOWLIST", " C:\\a ; ;C:\\b ", () => VspOptions.FromEnvironment());

        Assert.Equal(["C:\\a", "C:\\b"], options.AppAllowlist);

        Assert.Equal("p1", WithEnv("VSP_PROFILE", " p1 ", () => VspOptions.FromEnvironment().Profile));
        Assert.Equal("default", WithEnv("VSP_PROFILE", "  ", () => VspOptions.FromEnvironment().Profile));
    }
}
