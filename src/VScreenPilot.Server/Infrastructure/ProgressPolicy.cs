namespace VScreenPilot.Server.Infrastructure;

/// <summary>
/// 进度通知的全局开关。
/// 背景：客户端可能给出 progressToken，却无法关联服务端发回的通知（会把传输判为错误并断开）。
/// 因此默认不发送，只有显式设置 VSP_PROGRESS=1 才启用；启用后仍要求客户端提供 token。
/// </summary>
internal static class ProgressPolicy
{
    public static bool Enabled { get; } = EnvFlags.IsTruthyLoose(
        Environment.GetEnvironmentVariable("VSP_PROGRESS"));

    public static string Description => Enabled ? "enabled" : "disabled";
}
