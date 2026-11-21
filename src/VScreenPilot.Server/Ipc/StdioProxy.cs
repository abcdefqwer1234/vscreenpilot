using System.IO.Pipes;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Mcp;

namespace VScreenPilot.Ipc;

/// <summary>
/// 非首个 MCP 客户端的瘦代理：握手后把 stdio 原样转发到 daemon 的 MCP 管道。
/// 不做 JSON 解析，不碰互斥体/会话/显示。
/// </summary>
internal static class StdioProxy
{
    public static int? TryRun(string profile, LogRing log, bool allowScript)
    {
        NamedPipeClientStream? pipe = null;
        bool connected = false;

        try
        {
            pipe = new NamedPipeClientStream(
                ".", PipeNames.Mcp(profile), PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect(300);
            connected = true;

            string cwd = Environment.GetEnvironmentVariable("VSP_CLIENT_CWD") is { Length: > 0 } configured
                ? configured
                : Environment.CurrentDirectory;

            try
            {
                cwd = Path.GetFullPath(cwd);
            }
            catch
            {
                cwd = Environment.CurrentDirectory;
            }

            var allowlist = new JsonArray();

            foreach (string part in (Environment.GetEnvironmentVariable("VSP_APP_ALLOWLIST") ?? string.Empty)
                         .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                allowlist.Add(part);
            }

            Handshake.WriteLineAsync(pipe, new JsonObject
            {
                ["vsp"] = 1,
                ["client"] = "mcp",
                ["proxy_pid"] = Environment.ProcessId,
                ["cwd"] = cwd,
                ["roots"] = new JsonArray(cwd),
                ["version"] = BuildInfo.Version,
                ["capabilities"] = new JsonObject
                {
                    ["allow_script"] = allowScript,
                    ["allow_unsafe_script"] = EnvFlags.IsTruthy(
                        Environment.GetEnvironmentVariable("VSP_ALLOW_UNSAFE_SCRIPT")),
                    ["app_allowlist"] = allowlist,
                },
            }).GetAwaiter().GetResult();

            using var responseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            string? responseLine = Handshake.ReadLineAsync(pipe, responseCts.Token)
                .GetAwaiter().GetResult();
            JsonObject? response = responseLine is null
                ? null
                : JsonNode.Parse(responseLine) as JsonObject;

            if (response?["ok"]?.GetValue<bool>() != true)
            {
                Console.Error.WriteLine(
                    $"VScreenPilot: 无法加入已有实例: {responseLine ?? "no handshake response"}");
                return 3;
            }

            string daemonVersion = response["daemon_version"]?.GetValue<string>() ?? string.Empty;

            if (!string.Equals(daemonVersion, BuildInfo.Version, StringComparison.Ordinal))
            {
                Console.Error.WriteLine(
                    $"VScreenPilot: E_VERSION_MISMATCH daemon={daemonVersion} client={BuildInfo.Version}；" +
                    "请关闭旧实例后重试");
                return 3;
            }

            log.Info($"已连接到共享实例: client_id={response["client_id"]}");

            Stream stdin = Console.OpenStandardInput();
            Stream stdout = Console.OpenStandardOutput();

            Task up = stdin.CopyToAsync(pipe);
            Task down = pipe.CopyToAsync(stdout);

            Task completed = Task.WhenAny(up, down).GetAwaiter().GetResult();

            if (completed == up && up.IsFaulted)
            {
                return Fail($"共享实例写入失败: {Describe(up.Exception)}");
            }

            if (completed == down)
            {
                // daemon 在客户端 stdin 仍打开时结束：正常 stop 或崩溃都属于“连接中断”，
                // 不能返回 0，否则客户端会误以为 MCP 服务正常退出。
                return Fail(
                    down.IsFaulted
                        ? $"共享实例连接中断: {Describe(down.Exception)}"
                        : "共享实例已断开（daemon 退出或崩溃）");
            }

            // 客户端 stdin EOF：等待 daemon 把剩余输出写完一小段时间。
            // 若 daemon 在这段时间内崩溃，必须返回 3，而不是伪装成正常退出。
            try
            {
                down.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                return 0;
            }
            catch (TimeoutException)
            {
                return 0;
            }
            catch (Exception exception)
            {
                return Fail($"共享实例连接中断: {exception.GetBaseException().Message}");
            }
        }
        catch (TimeoutException)
        {
            return connected ? Fail("共享实例握手超时") : null;
        }
        catch (IOException exception)
        {
            // 已经连上再失败，说明 daemon 中途退出/管道断开；此时不能回退成新 daemon，
            // 否则已经被消费的 initialize 会丢失，客户端只能等到超时。
            return connected ? Fail($"共享实例连接中断: {exception.Message}") : null;
        }
        catch (Exception exception)
        {
            try
            {
                Console.Error.WriteLine($"VScreenPilot: 代理失败: {exception.Message}");
            }
            catch (Exception writeError)
            {
                // 无控制台时忽略。
                _ = writeError;
            }

            return 3;
        }
        finally
        {
            pipe?.Dispose();
        }
    }

    private static string Describe(Exception? exception) =>
        exception?.GetBaseException().Message ?? "unknown error";

    private static int Fail(string message)
    {
        try
        {
            Console.Error.WriteLine($"VScreenPilot: {message}");
        }
        catch (Exception exception)
        {
            // 无控制台时忽略。
            _ = exception;
        }

        return 3;
    }
}
