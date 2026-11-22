using System.Text.Json.Nodes;
using VScreenPilot.Composition;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using static VScreenPilot.Composition.InstanceGuard;
using VScreenPilot.Ipc;
using static VScreenPilot.Cli.ControlOutput;

namespace VScreenPilot.Cli;

/// <summary>c tl 子命令参数。</summary>
internal sealed record ControlCommand(
    string Name, string Profile, bool ForceStop, bool Ensure, bool IncludeAll);

/// <summary>有实例时通过 ctl 管道执行 env/snapshot/screen/status/stop。</summary>
internal static class ControlInvoker
{
    public static int Run(ControlCommand request, LogRing log, out bool needsLocalSession)
    {
        string command = request.Name;
        string profile = request.Profile;
        bool forceStop = request.ForceStop;
        bool ensure = request.Ensure;
        bool includeAll = request.IncludeAll;
        needsLocalSession = false;
        string op = command.Trim().ToLowerInvariant();
        int retries = ResolveRetries(profile);

        switch (op)
        {
            case "env":
                return RunEnv(profile, log, ensure, retries);
            case "snapshot":
            case "screen":
                return RunCapture(op, profile, log, includeAll, retries, out needsLocalSession);
            case "status":
                return RunStatus(profile, log, retries);
            case "stop":
                return RunStop(profile, log, forceStop, retries);
            default:
                return Unknown(command);
        }
    }

    /// <summary>有实例时多试几次，覆盖 daemon 刚启动/ctl 刚处理完上一个请求的窗口。</summary>
    private static int ResolveRetries(string profile)
    {
        InstanceRegistry.Entry? known = IsMutexHeld(profile)
            ? new InstanceRegistry(profile).Read()
            : null;
        bool starting = known is { Ready: false } &&
                        string.Equals(known.Version, BuildInfo.Version, StringComparison.Ordinal);
        return starting ? 25 : IsMutexHeld(profile) ? 5 : 1;
    }

    private static JsonObject? Invoke(string profile, JsonObject request, int retries, int timeoutMs = 5000) =>
        ControlClient.TryInvoke(profile, request, timeoutMs: timeoutMs, retries: retries);

    private static int RunEnv(string profile, LogRing log, bool ensure, int retries)
    {
        JsonObject? response = Invoke(profile, new JsonObject
        {
            ["vsp"] = 1,
            ["op"] = "env",
            ["ensure"] = ensure,
        }, retries);

        if (response is not null)
        {
            return PrintControlResponse(response);
        }

        if (IsMutexHeld(profile))
        {
            ReportInstanceBusy(profile, "已有活跃实例，但没有可用的控制管道（可能是旧版本）", log);
            return 3;
        }

        PrintJson(CliEnvironment.ReadOnly(ensure));
        return 0;
    }

    private static int RunCapture(
        string op, string profile, LogRing log, bool includeAll, int retries, out bool needsLocalSession)
    {
        needsLocalSession = false;
        JsonObject? response = Invoke(profile, new JsonObject
        {
            ["vsp"] = 1,
            ["op"] = op,
            ["interactive_only"] = !includeAll,
        }, retries, timeoutMs: 30000);

        if (response is not null)
        {
            return PrintControlResponse(response);
        }

        if (IsMutexHeld(profile))
        {
            ReportInstanceBusy(profile, "已有活跃实例，但没有可用的控制管道（可能是旧版本）", log);
            return 3;
        }

        needsLocalSession = true;
        return -1;
    }

    private static int RunStatus(string profile, LogRing log, int retries)
    {
        JsonObject? response = Invoke(profile, new JsonObject
        {
            ["vsp"] = 1,
            ["op"] = "status",
        }, retries);

        if (response is not null)
        {
            return PrintControlResponse(response);
        }

        if (IsMutexHeld(profile))
        {
            ReportInstanceBusy(profile, "实例存在，但没有可用的控制管道（可能是旧版本）", log);
            return 3;
        }

        PrintNoInstance();
        return 3;
    }

    private static int RunStop(string profile, LogRing log, bool forceStop, int retries)
    {
        JsonObject? response = Invoke(profile, new JsonObject
        {
            ["vsp"] = 1,
            ["op"] = "stop",
            ["force"] = forceStop,
        }, retries);

        if (response is not null)
        {
            return PrintControlResponse(response);
        }

        if (forceStop && TryForceStop(profile, log))
        {
            return 0;
        }

        if (IsMutexHeld(profile))
        {
            ReportInstanceBusy(profile, "实例存在，但控制管道不可用（可能是旧版本）", log);
            return 3;
        }

        PrintNoInstance();
        return 3;
    }

    /// <summary>`--cli stop --force`：控制管道不可用时按 registry 的 pid 结束进程。</summary>
    private static bool TryForceStop(string profile, LogRing log)
    {
        InstanceRegistry.Entry? owner = new InstanceRegistry(profile).Read();

        if (owner is null || owner.Pid <= 0 || owner.Pid == Environment.ProcessId)
        {
            return false;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(owner.Pid);
            process.Kill();
            PrintJson(new JsonObject
            {
                ["ok"] = true,
                ["stopped"] = true,
                ["forced"] = true,
                ["pid"] = owner.Pid,
            });
            return true;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"强制结束 pid={owner.Pid} 失败: {exception.Message}");
            return false;
        }
    }

    private static void PrintNoInstance() =>
        PrintJson(new JsonObject
        {
            ["ok"] = false,
            ["code"] = "E_NO_INSTANCE",
            ["message"] = "当前 profile 没有活跃实例",
        });

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知 --cli 命令: {command}（可选 env|snapshot|screen|status|stop）");
        return 2;
    }
}
