namespace VScreenPilot.Input;

/// <summary>
/// 输入通道策略。默认不自动降级到 SendInput：那会移动真实光标并抢焦点，
/// 必须由调用方显式传 fallback_input=true，或用 VSP_INPUT_AUTOFALLBACK=1 进程级开启。
/// </summary>
internal static class InputPolicy
{
    internal const string AutoFallbackEnv = "VSP_INPUT_AUTOFALLBACK";

    /// <summary>进程启动时读取一次；运行中改环境变量不生效，与其它 VSP_* 开关一致。</summary>
    internal static readonly bool AutoFallback =
        ParseAutoFallback(Environment.GetEnvironmentVariable(AutoFallbackEnv));

    internal static bool ParseAutoFallback(string? raw) =>
        raw is not null &&
        (raw.Trim().Equals("1", StringComparison.OrdinalIgnoreCase) ||
         raw.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) ||
         raw.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase));

    /// <summary>显式请求或进程级开关任一开启即可降级到 SendInput。</summary>
    internal static bool AllowsInputFallback(bool requested) => requested || AutoFallback;
}
