using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Session;
using static VScreenPilot.Cli.CommandLineOptions;

namespace VScreenPilot.Cli;

/// <summary>
/// 无实例时的 CLI 伴生入口：同一 exe 不经过 MCP 也能取环境/快照/截图。
/// 编码代理用 CLI + skill 可以省掉工具 schema 的上下文成本。
/// </summary>
internal static class LocalSessionInvoker
{
    /// <summary>
    /// CLI 伴生入口：同一 exe 不经过 MCP 也能取环境/快照/截图。
    /// 编码代理用 CLI + skill 可以省掉工具 schema 的上下文成本。
    /// </summary>
    public static int Run(
        string command, string[] args, SessionManager session,
        LogRing log, JobObject job, MutexGuard mutex)
    {
        try
        {
            JsonObject result;

            switch (command.Trim().ToLowerInvariant())
            {
                case "env":
                    result = VspJson.ToNode(session.GetEnvironment(HasFlag(args, "--ensure"))).AsObject();
                    break;

                case "snapshot":
                    SnapshotResult snapshot = session.Snapshot(
                        pid: null,
                        maxDepth: 4,
                        interactiveOnly: !HasFlag(args, "--all"),
                        maxNodes: 300,
                        includeValues: true,
                        includeScreenshot: false,
                        scale: 0.5);
                    result = VspJson.ToNode(snapshot.Data).AsObject();
                    break;

                case "screen":
                    SessionManager.CaptureResult capture = session.CaptureScreenEx(
                        null, null, null, null, 0.5, savePath: null,
                        storeFrame: false, previewScale: null);
                    string path = Path.Combine(
                        session.ArtifactDirectory,
                        $"cli-{DateTime.Now:HHmmss-fff}.png");
                    File.WriteAllBytes(path, capture.Png);
                    JsonObject meta = VspJson.ToNode(capture.Meta).AsObject();
                    meta["path"] = path;
                    result = meta;
                    break;

                default:
                    Console.Error.WriteLine(
                        $"未知 --cli 命令: {command}（可选 env|snapshot|screen）");
                    return 2;
            }

            Console.Out.WriteLine(
                result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            var error = new JsonObject
            {
                ["ok"] = false,
                ["error"] = exception.Message,
            };
            Console.Out.WriteLine(
                error.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            log.Error("CLI 执行失败", exception);
            return 1;
        }
        finally
        {
            session.Dispose();
            job.Dispose();
            mutex.Dispose();
            log.Dispose();
        }
    }
}
