using VScreenPilot.Platform.Logging;
namespace VScreenPilot.Platform.Security;

/// <summary>
/// 进程是否以管理员身份运行。取不到身份（受限令牌、异常）时一律按未提权处理。
/// </summary>
public static class Elevation
{
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return false;
        }
    }
}
