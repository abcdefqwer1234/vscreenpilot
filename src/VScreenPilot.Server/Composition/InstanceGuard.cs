using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;

namespace VScreenPilot.Composition;

/// <summary>单实例互斥体探测与“实例已占用”诊断输出。</summary>
internal static class InstanceGuard
{
    public static bool IsMutexHeld(string profile)
    {
        string name = Profile.MutexName(profile);
        string[] candidates = string.Equals(profile, "default", StringComparison.Ordinal)
            ? new[] { $@"Global\{name}" }
            : new[] { $@"Global\{name}", $@"Local\{name}" };
        bool inaccessible = false;

        foreach (string candidate in candidates)
        {
            try
            {
                using var mutex = new Mutex(false, candidate);

                try
                {
                    if (mutex.WaitOne(0))
                    {
                        mutex.ReleaseMutex();
                        return false;
                    }
                }
                catch (AbandonedMutexException)
                {
                    try
                    {
                        mutex.ReleaseMutex();
                    }
                    catch (Exception exception)
                    {
                        // 忽略。
                        _ = exception;
                    }

                    return false;
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                inaccessible = true;
            }
            catch (Exception exception)
            {
                // 继续尝试下一个命名空间。
                _ = exception;
            }
        }

        return inaccessible;
    }

    public static void ReportInstanceBusy(string profile, string? error, LogRing log)
    {
        var registry = new InstanceRegistry(profile);
        InstanceRegistry.Entry? entry = registry.Read();

        var payload = new JsonObject
        {
            ["code"] = "E_INSTANCE_BUSY",
            ["profile"] = profile,
            ["mutex"] = $@"Global\{Profile.MutexName(profile)}",
            ["owner"] = entry?.ToJson(),
            ["error"] = error,
            ["hint"] = "关闭占用实例的 MCP 客户端；旧版本可能需要关闭其父进程或用任务管理器结束 owner.pid",
        };

        try
        {
            Console.Error.WriteLine("VScreenPilot: 实例已被占用 (E_INSTANCE_BUSY)");
            Console.Error.WriteLine($"  profile: {profile}");
            Console.Error.WriteLine($"  mutex: Global\\{Profile.MutexName(profile)}");

            if (entry is not null)
            {
                Console.Error.WriteLine(
                    $"  owner: pid={entry.Pid}, started={entry.StartedAt}, " +
                    $"cwd={(string.IsNullOrWhiteSpace(entry.Cwd) ? "unknown" : entry.Cwd)}, " +
                    $"version={entry.Version}, elevated={entry.Elevated}");
            }
            else
            {
                Console.Error.WriteLine("  owner: unknown（可能是旧版本/其他用户）");
            }

            Console.Error.WriteLine("  解法:");
            Console.Error.WriteLine("    1) 关闭占用实例的 MCP 客户端（旧版本需要关闭其父进程）");
            Console.Error.WriteLine("    2) 任务管理器结束 owner.pid（需相同权限）");
            Console.Error.WriteLine(payload.ToJsonString());
        }
        catch (Exception exception)
        {
            // 无控制台时忽略。
            _ = exception;
        }

        log.Warn($"实例已被占用: {error}");
    }
}
