using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Diagnostics;
using VScreenPilot.Session;

namespace VScreenPilot.Cli;

/// <summary>
/// 无 daemon 时的只读环境查询：不构造 SessionManager，因此不会触发
/// RecoverFromPreviousCrash、不会挂屏，也不会清 state。
/// </summary>
internal static class CliEnvironment
{
    public static JsonObject ReadOnly(bool ensure = false)
    {
        using var log = new LogRing();
        var state = new StateStore(log);
        string profile = Profile.Resolve();

        var result = new JsonObject
        {
            ["ok"] = true,
            ["version"] = BuildInfo.Version,
            ["ensure_ignored"] = ensure,

            ["profile"] = profile,
            ["mutex"] = $@"Global\{Profile.MutexName(profile)}",
            ["cwd"] = Environment.CurrentDirectory,
            ["log_file"] = log.FilePath,
            ["state_file"] = state.FilePath,
            ["state"] = state.Describe(),
            ["artifacts"] = ArtifactSummary.Describe(profile),
        };

        if (ensure)
        {
            result["warning"] = "无 daemon 的本地只读查询不会挂载虚拟屏；请在 MCP 中调用 get_environment{ensure:true}";
        }

        string driverStatus;

        try
        {
            driverStatus = VddDevice.QueryStatus().ToString();
        }
        catch (Exception exception)
        {
            driverStatus = $"Error: {exception.Message}";
        }

        var driver = new JsonObject { ["status"] = driverStatus };

        try
        {
            using var vdd = new VddDevice();

            if (vdd.TryOpen(out int openError))
            {
                int version = vdd.GetVersion();
                driver["open"] = true;
                driver["version"] = version >= 0
                    ? $"{((version >> 16) & 0xFFFF)}.{version & 0xFFFF}"
                    : null;
            }
            else
            {
                driver["open"] = false;
                driver["open_error"] = openError;
            }
        }
        catch (Exception exception)
        {
            driver["open"] = false;
            driver["open_error"] = exception.Message;
        }

        result["driver"] = driver;

        string? ownDevice = state.Load()?.DisplayDevice;
        DisplayInfo? parsec = !string.IsNullOrWhiteSpace(ownDevice)
            ? DisplayTopology.FindParsecDisplayByDevice(ownDevice)
            : string.Equals(profile, "default", StringComparison.Ordinal)
                ? DisplayTopology.FindParsecDisplay()
                : null;
        result["virtual_display"] = parsec is null
            ? new JsonObject { ["enabled"] = false }
            : new JsonObject
            {
                ["enabled"] = true,
                ["device"] = parsec.DeviceName,
                ["x"] = parsec.X,
                ["y"] = parsec.Y,
                ["width"] = parsec.Width,
                ["height"] = parsec.Height,
                ["dpi"] = parsec.Dpi,
            };

        DisplayInfo? primary = DisplayTopology.FindPrimary();
        result["primary"] = primary is null
            ? null
            : new JsonObject
            {
                ["device"] = primary.DeviceName,
                ["x"] = primary.X,
                ["y"] = primary.Y,
                ["width"] = primary.Width,
                ["height"] = primary.Height,
                ["dpi"] = primary.Dpi,
            };

        (int x, int y, int width, int height) = DisplayTopology.VirtualScreenBounds();
        result["virtual_screen"] = new JsonObject
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["monitors"] = NativeMethods.GetSystemMetrics(80),
        };

        return result;
    }
}
