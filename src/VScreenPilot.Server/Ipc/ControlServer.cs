using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Session;
using VScreenPilot.Services;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Platform.Ipc;
using VScreenPilot.Platform.Windows;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Ipc;

/// <summary>
/// 只读诊断/控制管道。活跃实例在 <c>\\.\pipe\VScreenPilot-ctl-&lt;profile&gt;-&lt;sid&gt;</c>
/// 上响应 env/snapshot/screen/status/stop；除 stop 外的操作不改变会话状态。
/// </summary>
internal sealed class ControlServer : IDisposable
{
    internal const int MaxRequestBytes = 1024 * 1024;
    internal const int MaxResponseBytes = 4 * 1024 * 1024;

    internal readonly ISessionFacade _facade;
    internal readonly IControlService _control;
    internal readonly LogRing _log;
    internal readonly InstanceRegistry _registry;
    internal readonly string _pipeName;
    internal readonly Action _shutdown;
    internal readonly CancellationTokenSource _cts = new();
    internal readonly ControlCommands Commands;
    internal Task[]? _loops;
    internal int _disposed;

    // 两个监听循环：一个较慢的 --cli screen/snapshot 不会让 env/status 连不上。
    // Windows 的 maxNumberOfServerInstances 计的是“连接中 + 监听中”，
    // 因此 2 个循环各保留 1 个监听实例、最多 2 个连接，取 4。
    internal const int ListerCount = 2;
    internal const int MaxInstances = 4;

    public ControlServer(
        ISessionFacade facade,
        IControlService control,
        LogRing log,
        InstanceRegistry registry,
        string profile,
        Action shutdown)
    {
        Commands = new ControlCommands(this);
        _facade = facade;
        _control = control;
        _log = log;
        _registry = registry;
        _pipeName = PipeNames.Ctl(profile);
        _shutdown = shutdown;
    }

    public string PipeName => _pipeName;

    public void Start()
    {
        _loops = Enumerable.Range(0, ListerCount)
            .Select(_ => Task.Run(() => LoopAsync(_cts.Token)))
            .ToArray();
        _log.Info($"控制管道已启动: {_pipeName}");
    }

    internal async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
#pragma warning disable CA2007 // await using 无法附加 ConfigureAwait；管道等待随后显式 ConfigureAwait(false)
                await using var pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
#pragma warning restore CA2007

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                if (!IntegrityCheck.TryCheck(pipe.SafePipeHandle, out string integrityReason))
                {
                    _log.Warn($"拒绝控制连接：{integrityReason}");
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                    {
                        AutoFlush = true,
                    };
                    await WriteAsync(writer, Error("E_INTEGRITY_MISMATCH", integrityReason)).ConfigureAwait(false);
                    continue;
                }

                await HandleAsync(pipe, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _log.Warn("控制请求超时/取消，继续监听");
                continue;
            }
            catch (Exception exception)
            {
                _log.Warn($"控制管道连接失败: {exception.Message}");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    internal async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
        };

        using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        requestCts.CancelAfter(TimeSpan.FromSeconds(5));

        string? line;

        try
        {
            line = await ReadLimitedLineAsync(reader, MaxRequestBytes, requestCts.Token).ConfigureAwait(false);
        }
        catch (ToolException exception)
        {
            await WriteAsync(writer, Error(exception.Code, exception.Message)).ConfigureAwait(false);
            return;
        }

        if (line is null)
        {
            await WriteAsync(writer, Error("E_INVALID_ARG", "控制请求为空或超过 1MB")).ConfigureAwait(false);
            return;
        }

        JsonObject? request;

        try
        {
            request = JsonNode.Parse(line) as JsonObject;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            request = null;
        }

        if (request is null)
        {
            await WriteAsync(writer, Error("E_INVALID_ARG", "控制请求不是有效 JSON")).ConfigureAwait(false);
            return;
        }

        string op = request["op"] is JsonValue opValue && opValue.TryGetValue(out string? opText)
            ? opText
            : string.Empty;

        try
        {
            using IDisposable controlScope = _control.UseClient(
                new ClientInfo("ctl", Environment.CurrentDirectory, Array.Empty<string>(), "control", true));

            bool shutdownRequested = false;
            JsonObject result = op switch
            {
                "env" => Commands.EnvironmentResult(request),
                "snapshot" => Commands.Snapshot(request),
                "screen" => Commands.Capture(),
                "status" => Commands.Status(),
                "stop" => Commands.Stop(request["force"]?.GetValue<bool>() ?? false, out shutdownRequested),
                _ => throw new ToolException(
                    "E_INVALID_ARG",
                    $"未知控制操作: {op}（可选 env/snapshot/screen/status/stop）"),
            };

            await WriteAsync(writer, new JsonObject
            {
                ["vsp"] = 1,
                ["ok"] = true,
                ["result"] = result,
            }).ConfigureAwait(false);

            if (shutdownRequested)
            {
                // 先让响应 flushed，再退出；否则 --cli stop 可能只看到 EOF。
                _ = Task.Run(() =>
                {
                    try
                    {
                        _shutdown();
                    }
                    catch (Exception exception)
                    {
                        // 退出路径忽略。
                        LogSink.Debug($"忽略异常: {exception.Message}");
                    }
                }, CancellationToken.None);
            }
        }
        catch (ToolException exception)
        {
            await WriteAsync(writer, new JsonObject
            {
                ["vsp"] = 1,
                ["ok"] = false,
                ["code"] = exception.Code,
                ["message"] = exception.Message,
                ["hint"] = exception.Hint,
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _log.Error($"控制操作 {op} 失败", exception);
            await WriteAsync(writer, Error("E_INTERNAL", exception.Message)).ConfigureAwait(false);
        }
    }

    internal static async Task<string?> ReadLimitedLineAsync(
        StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var line = new StringBuilder();
        char[] buffer = new char[4096];

        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToString().TrimEnd('\r');
            }

            for (int index = 0; index < read; index++)
            {
                char value = buffer[index];

                if (value == '\n')
                {
                    return line.ToString().TrimEnd('\r');
                }

                if (line.Length >= maxChars)
                {
                    throw new ToolException("E_INVALID_ARG", "控制请求超过 1MB");
                }

                line.Append(value);
            }
        }
    }

    internal static async Task WriteAsync(StreamWriter writer, JsonObject payload)
    {
        string json = payload.ToJsonString();

        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
        {
            json = Error(
                "E_RESPONSE_TOO_LARGE",
                $"控制响应超过 {MaxResponseBytes / (1024 * 1024)}MB；请减小 max_nodes/查询范围").ToJsonString();
        }

        await writer.WriteLineAsync(json).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    internal static JsonObject Error(string code, string message) => new()
    {
        ["vsp"] = 1,
        ["ok"] = false,
        ["code"] = code,
        ["message"] = message,
    };

    internal double CurrentWorkingSetMb()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return Math.Round(process.WorkingSet64 / 1048576.0, 1);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return 0;
        }
    }

    internal int CurrentHandleCount()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            return process.HandleCount;
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return 0;
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
        catch (ObjectDisposedException exception)
        {
            // 已经取消。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        // 不立即 Dispose：HandleAsync 可能还在创建 linked token；进程退出时由 OS 回收。
    }
}
