using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Ipc;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Interop;
using VScreenPilot.Platform.Security;
using VScreenPilot.Interop;
using VScreenPilot.Session;

namespace VScreenPilot.Diagnostics;

/// <summary>`--doctor` 的只读诊断输出。字段、checks 与退出码属于冻结契约。</summary>
internal static class DoctorReportBuilder
{
    public static int Run(LogRing log, bool allowScript, string profile)
    {
        var checks = new JsonArray();
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

        result["instances"] = instances;

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            result["process"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["working_set_mb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1),
                ["handles"] = process.HandleCount,
            };
        }
        catch (Exception exception)
        {
            // 诊断字段缺失不影响其余结果。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
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

        bool driverOk = string.Equals(driverStatus, "Ok", StringComparison.Ordinal);
        bool driverOpen = driver["open"]?.GetValue<bool>() == true;

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "driver.status",
            ["ok"] = driverOk,
            ["detail"] = driverStatus,
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

        string powerPointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft Office", "root", "Office16", "POWERPNT.EXE");
        bool powerPointFound = File.Exists(powerPointPath);

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "office.powerpoint",
            ["ok"] = powerPointFound,
            ["detail"] = powerPointFound ? powerPointPath : "not found (Office 未安装或路径不同)",
        });

        bool artifactsWritable;
        string artifactsDetail;

        try
        {
            string probeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot", "artifacts", "doctor");
            Directory.CreateDirectory(probeDirectory);
            string probeFile = Path.Combine(probeDirectory, $"probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            artifactsWritable = true;
            artifactsDetail = probeDirectory;
        }
        catch (Exception exception)
        {
            artifactsWritable = false;
            artifactsDetail = exception.Message;
        }

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "artifacts.writable",
            ["ok"] = artifactsWritable,
            ["detail"] = artifactsDetail,
        });

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

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "virtual_display",
            ["ok"] = parsec is not null,
            ["detail"] = parsec is null ? "not mounted" : $"{(int)parsec.X},{(int)parsec.Y} {parsec.Width}x{parsec.Height}",
        });

        result["checks"] = checks;

        // 旧实现只看 driver.status，忽略了 driver.open=false，会误报 OK。
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
