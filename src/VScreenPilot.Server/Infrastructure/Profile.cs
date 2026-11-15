using System.Text.RegularExpressions;

namespace VScreenPilot.Infrastructure;

/// <summary>
/// 多会话 profile：不同 profile 使用独立互斥体、状态目录与日志目录，
/// 因而可以各挂一块虚拟屏并行工作；输入所有权仍由同一台桌面的前台决定。
/// </summary>
internal static partial class Profile
{
    public const string EnvironmentVariable = "VSP_PROFILE";

    public static string Resolve()
    {
        string raw = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? "default";
        string clean = Sanitize(raw);
        return clean.Length == 0 ? "default" : clean;
    }

    public static string MutexName(string profile) =>
        profile == "default" ? "VScreenPilot_Session_0" : $"VScreenPilot_Session_{profile}";

    public static string StateFileName(string profile) =>
        profile == "default" ? "state.json" : $"state.{profile}.json";

    private static string Sanitize(string value)
    {
        string lower = value.Trim().ToLowerInvariant();
        return NonNameCharacters().Replace(lower, "-").Trim('-', '_');
    }

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex NonNameCharacters();
}
