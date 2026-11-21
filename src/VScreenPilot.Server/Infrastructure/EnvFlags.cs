namespace VScreenPilot.Server.Infrastructure;

/// <summary>
/// 环境变量布尔值解析。严格/宽松两种口径必须分开保留：
/// <c>VSP_ALLOW_SCRIPT</c> 只认 1/true/yes，<c>VSP_ALLOW_UNSAFE_SCRIPT</c> 额外认 on 并忽略空白。
/// 合并成一种会悄悄放宽对外的取值语义。
/// </summary>
internal static class EnvFlags
{
    /// <summary>严格口径：1/true/yes，大小写不敏感，不做 trim。</summary>
    public static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>宽松口径：trim 后接受 1/true/yes/on。</summary>
    public static bool IsTruthyLoose(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
}
