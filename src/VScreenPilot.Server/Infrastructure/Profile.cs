using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VScreenPilot.Server.Infrastructure;

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

    private const int MaxLength = 32;

    private static string Sanitize(string value)
    {
        string lower = value.Trim().ToLowerInvariant();
        string clean = NonNameCharacters().Replace(lower, "-").Trim('-', '_');

        if (clean.Length <= MaxLength)
        {
            return clean;
        }

        // 保留可读前缀 + 短哈希，避免管道名/状态文件名超限。
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(clean)), 0, 4).ToLowerInvariant();
        return clean[..(MaxLength - hash.Length - 1)] + "-" + hash;
    }

    [GeneratedRegex("[^a-z0-9_-]+")]
    private static partial Regex NonNameCharacters();
}
