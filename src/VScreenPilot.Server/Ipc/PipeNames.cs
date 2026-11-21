using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Ipc;

/// <summary>
/// 命名管道名：按 profile + 当前用户 SID 哈希隔离。
/// 管道本身是 machine-wide 的，SID 后缀避免不同用户抢同名管道。
/// </summary>
internal static class PipeNames
{
    public static string Mcp(string profile) => $"VScreenPilot-mcp-{profile}-{SidHash()}";

    public static string Ctl(string profile) => $"VScreenPilot-ctl-{profile}-{SidHash()}";

    private static string SidHash()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            string? sid = identity.User?.Value;

            if (string.IsNullOrEmpty(sid))
            {
                return "user";
            }

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(sid));
            return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return "user";
        }
    }
}
