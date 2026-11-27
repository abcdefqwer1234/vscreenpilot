using System.Globalization;

namespace VScreenPilot.Options;

/// <summary>
/// 进程级配置的强类型视图。默认值与钳制范围必须与既有实现逐项一致：
/// 这是“重构不改外特性”的一部分，单项改动都要配一条表驱动测试。
/// </summary>
public sealed record VspOptions
{
    public int DisplayWidth { get; init; } = 1280;

    public int DisplayHeight { get; init; } = 720;

    public int DisplayRefresh { get; init; } = 60;

    public int? DisplayOffset { get; init; }

    public bool AllowScript { get; init; }

    public bool AllowUnsafeScript { get; init; }

    public string[] AppAllowlist { get; init; } = [];

    public bool InputAutoFallback { get; init; }

    public int ArtifactRetentionDays { get; init; } = 7;

    public long ScreenLeaseIdleMs { get; init; } = 60_000;

    public long ScreenLeaseWaitMs { get; init; }

    public int DaemonIdleMs { get; init; } = 300_000;

    public int ClientLimit { get; init; } = 16;

    public long TotalBufferBytes { get; init; } = 64L * 1024 * 1024;

    public long MaxMessageBytes { get; init; } = 16L * 1024 * 1024;

    public bool NoWarmup { get; init; }

    public string Profile { get; init; } = "default";

    /// <summary>从环境变量解析；同名变量的默认值/范围与旧实现逐字一致。</summary>
    public static VspOptions FromEnvironment()
    {
        return new VspOptions
        {
            DisplayWidth = ReadInt("VSP_DISPLAY_WIDTH", 1280, 800, 3840),
            DisplayHeight = ReadInt("VSP_DISPLAY_HEIGHT", 720, 600, 2160),
            DisplayRefresh = ReadInt("VSP_DISPLAY_REFRESH", 60, 30, 240),
            DisplayOffset = ReadNullableInt("VSP_DISPLAY_OFFSET"),
            AllowScript = ReadTruthy("VSP_ALLOW_SCRIPT"),
            AllowUnsafeScript = ReadTruthyLoose("VSP_ALLOW_UNSAFE_SCRIPT"),
            AppAllowlist = ReadList("VSP_APP_ALLOWLIST"),
            InputAutoFallback = ReadTruthy("VSP_INPUT_AUTOFALLBACK"),
            ArtifactRetentionDays = ReadInt("VSP_ARTIFACT_RETENTION_DAYS", 7, 0, int.MaxValue),
            ScreenLeaseIdleMs = ReadLong("VSP_SCREEN_LEASE_IDLE_MS", 60_000, 1_000, 600_000),
            ScreenLeaseWaitMs = ReadLong("VSP_SCREEN_LEASE_WAIT_MS", 0, 0, 120_000),
            DaemonIdleMs = ReadInt("VSP_DAEMON_IDLE_MS", 300_000, 0, int.MaxValue),
            ClientLimit = ReadInt("VSP_CLIENT_LIMIT", 16, 1, 64),
            TotalBufferBytes = ReadLong("VSP_TOTAL_BUFFER_MB", 64, 8, 512) * 1024 * 1024,
            MaxMessageBytes = ReadLong("VSP_MAX_MESSAGE_MB", 16, 1, 64) * 1024 * 1024,
            NoWarmup = string.Equals(
                Environment.GetEnvironmentVariable("VSP_NO_WARMUP"), "1", StringComparison.Ordinal),
            Profile = ResolveProfile(),
        };
    }

    private static string ResolveProfile()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_PROFILE");
        return string.IsNullOrWhiteSpace(raw) ? "default" : raw.Trim();
    }

    private static int ReadInt(string name, int fallback, int min, int max)
    {
        string? raw = Environment.GetEnvironmentVariable(name);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private static int? ReadNullableInt(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    private static long ReadLong(string name, long fallback, long min, long max)
    {
        string? raw = Environment.GetEnvironmentVariable(name);

        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    /// <summary>严格口径：1/true/yes，大小写不敏感，不做 trim（与 <c>VSP_ALLOW_SCRIPT</c> 一致）。</summary>
    private static bool ReadTruthy(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        return value is not null &&
               (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>宽松口径：trim 后接受 1/true/yes/on（与 <c>VSP_ALLOW_UNSAFE_SCRIPT</c> 一致）。</summary>
    private static bool ReadTruthyLoose(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);

        return !string.IsNullOrWhiteSpace(value) &&
               value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }

    private static string[] ReadList(string name)
    {
        string? raw = Environment.GetEnvironmentVariable(name);

        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
