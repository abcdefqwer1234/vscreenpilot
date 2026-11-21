using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Ipc;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Interop;
using VScreenPilot.Platform.Security;
using VScreenPilot.Interop;
using VScreenPilot.Session;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Diagnostics;

/// <summary>`--doctor` 的只读诊断输出。字段、checks 与退出码属于冻结契约。</summary>
internal static class DoctorReportBuilder
{
    public static int Run(LogRing log, bool allowScript, string profile)
    {
        var checks = new JsonArray();
        JsonObject result = BuildBase(log, allowScript, profile);
        result["instances"] = BuildInstances();
        JsonObject? process = ProbeProcess();

        if (process is not null)
        {
            result["process"] = process;
        }


        (JsonObject driver, bool driverOk, bool driverOpen) = ProbeDriver();
        result["driver"] = driver;

        DisplayInfo? parsec = BuildEnvironmentSections(result, profile, log);

        bool powerPointFound = ProbePowerPoint(out string powerPointDetail);
        (bool artifactsWritable, string artifactsDetail) = ProbeArtifactsWritable();

        AddChecks(checks, driver, driverOk, driverOpen, powerPointFound, powerPointDetail,
            artifactsWritable, artifactsDetail, parsec);
        result["checks"] = checks;

        // 旧实现只看 driver.status，忽略 driver.open=false，会误报 OK。
        bool ok = driverOk && driverOpen && Environment.UserInteractive;
        result["ok"] = ok;

        if (!ok)
        {
            result["hint"] = driverOk
                ? "驱动状态正常但设备打不开，或有检查项失败；查看 checks[]"
                : "驱动未就绪，请运行 scripts/install-driver.ps1";
        }

        Console.Out.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return result["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    private static JsonObject BuildBase(LogRing log, bool allowScript, string profile)
    {
        var result = new JsonObject
        {
            ["ok"] = true,
            ["version"] = BuildInfo.Version,
            ["profile"] = profile,
            ["platform"] = Environment.OSVersion.Platform.ToString(),
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["elevated"] = Elevation.IsProcessElevated(),
            ["interactive"] = Environment.UserInteractive,
            ["run_script_enabled"] = allowScript,
            ["app_allowlist"] = ReadAppAllowlist(),
            ["log_file"] = log.FilePath,
        };

        var registry = new InstanceRegistry(profile);
        result["registry"] = registry.Read()?.ToJson();

        JsonObject? ctlStatus = ControlClient.TryInvoke(
            profile,
            new JsonObject { ["vsp"] = 1, ["op"] = "status" },
            timeoutMs: 800,
            connectTimeoutMs: 150,
            retries: 1);

        if (ctlStatus?["ok"]?.GetValue<bool>() == true && ctlStatus["result"] is JsonObject status)
        {
            result["daemon"] = status.DeepClone();
        }

        return result;
    }

    private static JsonArray BuildInstances()
    {
        var instances = new JsonArray();
        var seenPids = new HashSet<int>();

        void AddInstance(InstanceRegistry.Entry instance)
        {
            if (seenPids.Add(instance.Pid))
            {
                instances.Add((JsonNode)instance.ToJson());
            }
        }

        foreach (InstanceRegistry.Entry instance in InstanceRegistry.ListAll())
        {
            AddInstance(instance);
        }

        foreach (InstanceRegistry.Entry instance in InstanceRegistry.ListStateEntries())
        {
            AddInstance(instance);
        }

        return instances;
    }

    private static JsonObject? ProbeProcess()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["working_set_mb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1),
                ["handles"] = process.HandleCount,
            };
        }
        catch (Exception exception)
        {
            // 诊断字段缺失不影响其余结果。
            LogSink.Debug($"忽略异常: {exception.Message}");
            return null;
        }
    }

    private static (JsonObject Driver, bool Ok, bool Open) ProbeDriver()
    {
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

        bool driverOk = string.Equals(driverStatus, "Ok", StringComparison.Ordinal);
        bool driverOpen = driver["open"]?.GetValue<bool>() == true;
        return (driver, driverOk, driverOpen);
    }

    /// <summary>virtual_display / primary / virtual_screen / state / artifacts 五段；返回本次枚举到的虚拟屏。</summary>
    private static DisplayInfo? BuildEnvironmentSections(JsonObject result, string profile, LogRing log)
    {
        string? ownDevice = new StateStore(log).Load()?.DisplayDevice;
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

        StateStore stateStore = new(log);
        result["state_file"] = stateStore.FilePath;
        result["state"] = stateStore.Describe();
        result["artifacts"] = ArtifactSummary.Describe(profile);
        return parsec;
    }

    private static bool ProbePowerPoint(out string detail)
    {
        string powerPointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft Office", "root", "Office16", "POWERPNT.EXE");
        bool found = File.Exists(powerPointPath);
        detail = found ? powerPointPath : "not found (Office 未安装或路径不同)";
        return found;
    }

    private static (bool Writable, string Detail) ProbeArtifactsWritable()
    {
        try
        {
            string probeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot", "artifacts", "doctor");
            Directory.CreateDirectory(probeDirectory);
            string probeFile = Path.Combine(probeDirectory, $"probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            return (true, probeDirectory);
        }
        catch (Exception exception)
        {
            return (false, exception.Message);
        }
    }

    private static void AddChecks(
        JsonArray checks, JsonObject driver, bool driverOk, bool driverOpen,
        bool powerPointFound, string powerPointDetail,
        bool artifactsWritable, string artifactsDetail, DisplayInfo? parsec)
    {
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "driver.status",
            ["ok"] = driverOk,
            ["detail"] = driver["status"]?.GetValue<string>(),
        });
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "driver.open",
            ["ok"] = driverOpen,
            ["detail"] = driverOpen ? driver["version"]?.GetValue<string>() : driver["open_error"]?.ToString(),
        });
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "session.interactive",
            ["ok"] = Environment.UserInteractive,
            ["detail"] = Environment.UserInteractive ? "ok" : "non-interactive session",
        });
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "office.powerpoint",
            ["ok"] = powerPointFound,
            ["detail"] = powerPointDetail,
        });
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "artifacts.writable",
            ["ok"] = artifactsWritable,
            ["detail"] = artifactsDetail,
        });
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "virtual_display",
            ["ok"] = parsec is not null,
            ["detail"] = parsec is null ? "not mounted" : $"{(int)parsec.X},{(int)parsec.Y} {parsec.Width}x{parsec.Height}",
        });
    }

    private static JsonArray ReadAppAllowlist()
    {
        var array = new JsonArray();
        string? raw = Environment.GetEnvironmentVariable("VSP_APP_ALLOWLIST");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return array;
        }

        foreach (string part in raw.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            array.Add(part);
        }

        return array;
    }
}
