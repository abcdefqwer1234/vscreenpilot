using System.Text.Json.Nodes;
using VScreenPilot.Composition;
using VScreenPilot.Infrastructure;
using static VScreenPilot.Composition.InstanceGuard;
using VScreenPilot.Ipc;
using static VScreenPilot.Cli.ControlOutput;

namespace VScreenPilot.Cli;

/// <summary>有实例时通过 ctl 管道执行 env/snapshot/screen/status/stop。</summary>
internal static class ControlInvoker
{
    public static int Run(
        string command, string profile, LogRing log, bool forceStop, bool ensure, bool includeAll,
        out bool needsLocalSession)
    {
        needsLocalSession = false;
        string op = command.Trim().ToLowerInvariant();

        // 有实例时多试几次，覆盖 daemon 刚启动/ctl 刚处理完上一个请求的窗口；
        // daemon 还在 ready=false 时按方案等待约 10s，避免启动竞态误报 E_INSTANCE_BUSY。
        InstanceRegistry.Entry? known = IsMutexHeld(profile)
            ? new InstanceRegistry(profile).Read()
            : null;
        bool starting = known is { Ready: false } &&
                        string.Equals(known.Version, BuildInfo.Version, StringComparison.Ordinal);
        int retries = starting ? 25 : IsMutexHeld(profile) ? 5 : 1;

        JsonObject? Invoke(JsonObject request, int timeoutMs = 5000) =>
            ControlClient.TryInvoke(
                profile, request, timeoutMs: timeoutMs, retries: retries);

        switch (op)
        {
            case "env":
            {
                JsonObject? response = Invoke(new JsonObject
                {
                    ["vsp"] = 1,
                    ["op"] = "env",
                    ["ensure"] = ensure,
                });

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

            case "snapshot":
            case "screen":
            {
                JsonObject? response = Invoke(new JsonObject
                {
                    ["vsp"] = 1,
                    ["op"] = op,
                    ["interactive_only"] = !includeAll,
                }, timeoutMs: 30000);

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

            case "status":
            {
                JsonObject? response = Invoke(new JsonObject
                {
                    ["vsp"] = 1,
                    ["op"] = "status",
                });

                if (response is not null)
                {
                    return PrintControlResponse(response);
                }

                if (IsMutexHeld(profile))
                {
                    ReportInstanceBusy(profile, "实例存在，但没有可用的控制管道（可能是旧版本）", log);
                    return 3;
                }

                PrintJson(new JsonObject
                {
                    ["ok"] = false,
                    ["code"] = "E_NO_INSTANCE",
                    ["message"] = "当前 profile 没有活跃实例",
                });
                return 3;
            }

            case "stop":
            {
                JsonObject? response = Invoke(new JsonObject
                {
                    ["vsp"] = 1,
                    ["op"] = "stop",
                    ["force"] = forceStop,
                });

                if (response is not null)
                {
                    return PrintControlResponse(response);
                }

                if (forceStop)
                {
                    InstanceRegistry.Entry? owner = new InstanceRegistry(profile).Read();

                    if (owner is not null && owner.Pid > 0 && owner.Pid != Environment.ProcessId)
                    {
                        try
                        {
                            using System.Diagnostics.Process process =
                                System.Diagnostics.Process.GetProcessById(owner.Pid);
                            process.Kill();
                            PrintJson(new JsonObject
                            {
                                ["ok"] = true,
                                ["stopped"] = true,
                                ["forced"] = true,
                                ["pid"] = owner.Pid,
                            });
                            return 0;
                        }
                        catch (Exception exception)
                        {
                            Console.Error.WriteLine($"强制结束 pid={owner.Pid} 失败: {exception.Message}");
                        }
                    }
                }

                if (IsMutexHeld(profile))
                {
                    ReportInstanceBusy(profile, "实例存在，但控制管道不可用（可能是旧版本）", log);
                    return 3;
                }

                PrintJson(new JsonObject
                {
                    ["ok"] = false,
                    ["code"] = "E_NO_INSTANCE",
                    ["message"] = "当前 profile 没有活跃实例",
                });
                return 3;
            }

            default:
                Console.Error.WriteLine($"未知 --cli 命令: {command}（可选 env|snapshot|screen|status|stop）");
                return 2;
        }
    }
}
