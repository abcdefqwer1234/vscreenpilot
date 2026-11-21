using System.IO.Pipes;
using System.Text.Json.Nodes;
using VScreenPilot.Infrastructure;
using VScreenPilot.Mcp;
using VScreenPilot.Session;

namespace VScreenPilot.Ipc;

/// <summary>
/// 首个进程内的 MCP 管道监听：每个连接一个独立 McpTransport（独立 client_id），
/// 共享同一个 SessionManager。这样第二个 MCP 客户端不再撞全局互斥体。
/// </summary>
internal sealed class PipeHost : IDisposable
{
    private readonly SessionManager _session;
    private readonly LogRing _log;
    private readonly TraceLog _trace;
    private readonly string _pipeName;
    private readonly int _maxClients;
    private readonly MessageBudget _messageBudget = new(MessageBudget.ResolveMaxBytes());
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;
    private int _clientSeq;
    private int _activeClients;
    private int _disposed;

    public PipeHost(SessionManager session, LogRing log, TraceLog trace, string profile)
    {
        _session = session;
        _log = log;
        _trace = trace;
        _pipeName = PipeNames.Mcp(profile);
        _maxClients = ResolveClientLimit();
    }

    private static int ResolveClientLimit()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_CLIENT_LIMIT");

        return int.TryParse(raw, out int value) ? Math.Clamp(value, 1, 64) : 16;
    }

    public string PipeName => _pipeName;

    public int ActiveClients => Volatile.Read(ref _activeClients);

    public void Start()
    {
        _loop = Task.Run(() => LoopAsync(_cts.Token));
        _log.Info($"MCP 共享管道已启动: {_pipeName}");
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;

            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    _maxClients + 2,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                NamedPipeServerStream connection = pipe;
                pipe = null;

                // 先占槽位再回到监听：握手不再阻塞 accept，也避免并发握手
                // 同时通过“未超限”检查。
                int slot = Interlocked.Increment(ref _activeClients);

                if (slot > _maxClients)
                {
                    Interlocked.Decrement(ref _activeClients);
                    _log.Warn($"拒绝共享连接：客户端已达上限 {_maxClients}");
                    _ = RejectAsync(
                        connection,
                        "E_CLIENT_LIMIT",
                        $"共享实例客户端已达上限 {_maxClients}",
                        cancellationToken);
                    continue;
                }

                _ = Task.Run(
                    () => HandleConnectionAsync(connection, cancellationToken),
                    CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();

                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _log.Warn("共享管道等待连接被取消，继续监听");
            }
            catch (Exception exception)
            {
                pipe?.Dispose();
                _log.Warn($"MCP 共享管道连接失败: {exception.Message}");

                try
                {
                    await Task.Delay(200, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task HandleConnectionAsync(
        NamedPipeServerStream connection, CancellationToken cancellationToken)
    {
        string clientId = "handshake";

        try
        {
            using (connection)
            {
                if (!IntegrityCheck.TryCheck(connection.SafePipeHandle, out string integrityReason))
                {
                    _log.Warn($"拒绝共享连接：{integrityReason}");
                    await Handshake.WriteLineAsync(connection, new JsonObject
                    {
                        ["vsp"] = 1,
                        ["ok"] = false,
                        ["code"] = "E_INTEGRITY_MISMATCH",
                        ["message"] = integrityReason,
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                ClientHello? hello;

                using (var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    handshakeCts.CancelAfter(TimeSpan.FromSeconds(5));
                    string? line = await Handshake.ReadLineAsync(connection, handshakeCts.Token)
                        .ConfigureAwait(false);
                    hello = Handshake.ParseClientHello(line);
                }

                if (hello is null)
                {
                    await Handshake.WriteLineAsync(connection, new JsonObject
                    {
                        ["vsp"] = 1,
                        ["ok"] = false,
                        ["code"] = "E_HANDSHAKE",
                        ["message"] = "握手失败：缺少/非法 VSP/1 客户端信息",
                    }, cancellationToken).ConfigureAwait(false);
                    return;
                }

                clientId = "c" + Interlocked.Increment(ref _clientSeq);
                var client = new ClientContext
                {
                    Id = clientId,
                    Name = hello.Client,
                    Cwd = hello.Cwd,
                    Roots = hello.Roots,
                    ProxyPid = hello.ProxyPid,
                    Version = hello.Version,
                    AllowScript = hello.AllowScript,
                    AllowUnsafeScript = hello.AllowUnsafeScript,
                    AppAllowlist = hello.AppAllowlist,
                };

                await Handshake.WriteLineAsync(connection, new JsonObject
                {
                    ["vsp"] = 1,
                    ["ok"] = true,
                    ["client_id"] = clientId,
                    ["protocol"] = 1,
                    ["daemon_version"] = BuildInfo.Version,
                }, cancellationToken).ConfigureAwait(false);

                using var limiter = new MessageLimiter(connection, _messageBudget);
                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var server = new McpTransport(_session, _log, client, _trace);
                await server.RunAsync(limiter, limiter, connectionCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception){
            // 进程退出/管道断开。
            _ = exception;
        }
        catch (Exception exception)
        {
            _log.Warn($"共享客户端 {clientId} 结束: {exception.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeClients);
            _log.Info($"共享客户端 {clientId} 断开");
        }
    }

    private async Task RejectAsync(
        NamedPipeServerStream connection,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        try
        {
            await Handshake.WriteLineAsync(connection, new JsonObject
            {
                ["vsp"] = 1,
                ["ok"] = false,
                ["code"] = code,
                ["message"] = message,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // 客户端可能已经断开。
            _ = exception;
        }
        finally
        {
            connection.Dispose();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException exception){
            // 已经取消。
            _ = exception;
        }

        // 不在 Dispose 立即释放 CTS：连接任务仍可能在创建 linked token；
        // 进程退出时由 OS 回收，避免 ObjectDisposedException 让计数泄漏。
    }
}
