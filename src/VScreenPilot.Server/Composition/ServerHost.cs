using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using VScreenPilot.Composition;
using VScreenPilot.Infrastructure;
using VScreenPilot.Ipc;
using VScreenPilot.Mcp;
using VScreenPilot.Session;

namespace VScreenPilot.Composition;

/// <summary>
/// 单一宿主：持有会话、控制管道、MCP 管道与实例注册表，负责启动顺序、
/// 预热、父进程监听、信号处理、空闲退出与关闭顺序。
/// </summary>
internal sealed class ServerHost : IDisposable
{
    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleCtrlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    private readonly SessionManager _session;
    private readonly LogRing _log;
    private readonly TraceLog? _trace;
    private readonly InstanceRegistry _registry;
    private readonly JobObject _job;
    private readonly MutexGuard _mutex;
    private readonly bool _allowScript;
    private readonly ControlServer _control;
    private readonly PipeHost _pipeHost;
    private ConsoleCtrlHandler? _ctrlHandler;

    public ServerHost(
        SessionManager session, LogRing log, TraceLog? trace, InstanceRegistry registry,
        JobObject job, MutexGuard mutex, bool allowScript)
    {
        _session = session;
        _log = log;
        _trace = trace;
        _registry = registry;
        _job = job;
        _mutex = mutex;
        _allowScript = allowScript;
        _control = new ControlServer(session, log, registry, session.ProfileName, Shutdown);
        _pipeHost = new PipeHost(session, log, trace!, session.ProfileName);
    }

    public void Dispose()
    {
        _pipeHost.Dispose();
        _control.Dispose();
        _session.Dispose();
        _registry.Delete();
    }

    private void Shutdown()
    {
        _pipeHost.Dispose();
        _control.Dispose();
        _registry.Delete();
        _session.Dispose();
        Environment.Exit(0);
    }

    public int Run(InstanceRegistry.Entry entry, string[] args)
    {
        _control.Start();
        _pipeHost.Start();

        entry.Ready = true;
        entry.DisplayDevice = _session.VirtualDisplay?.DeviceName ?? string.Empty;
        _registry.Write(entry);

        int? parentPid = GetParentPid(args);
        if (parentPid is not null)
        {
            ParentProcessWatcher.Start(
                parentPid.Value, _session, _log, () => _pipeHost.ActiveClients > 0);
        }

        // 启动时后台预热虚拟屏：把不可避免的第一次黑屏挪到客户端启动阶段。
        if (Environment.GetEnvironmentVariable("VSP_NO_WARMUP") != "1")
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _session.EnsureEnvironment();
                    _session.TrimIdleMemory("startup_warm");
                    entry.DisplayDevice = _session.VirtualDisplay?.DeviceName ?? string.Empty;
                    _registry.Write(entry);
                    _log.Info("虚拟屏预热完成");
                }
                catch (Exception exception)
                {
                    _log.Warn($"虚拟屏预热失败，将在首次使用时重试: {exception.Message}");
                }
            });
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _pipeHost.Dispose();
            _control.Dispose();
            _session.Dispose();
            _registry.Delete();
            _log.Dispose();
        };

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            Shutdown();
        };

        _ctrlHandler = _ =>
        {
            _pipeHost.Dispose();
            _control.Dispose();
            _session.Dispose();
            _registry.Delete();
            return false;
        };
        SetConsoleCtrlHandler(_ctrlHandler, true);

        PosixSignalRegistration? sigterm = null;

        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                Shutdown();
            });
        }
        catch (PlatformNotSupportedException)
        {
            // Windows 上部分场景不支持，忽略。
        }

        try
        {
            var server = new McpServer(_session, _log, _trace!);
            int exitCode = server.RunAsync().GetAwaiter().GetResult();

            int idleMs = ResolveDaemonIdleMs();
            DateTime? idleSince = null;

            while (true)
            {
                int active = _pipeHost.ActiveClients;

                if (active > 0)
                {
                    idleSince = null;
                }
                else if (idleMs == 0)
                {
                    break;
                }
                else
                {
                    idleSince ??= DateTime.UtcNow;

                    if (DateTime.UtcNow - idleSince.Value >= TimeSpan.FromMilliseconds(idleMs))
                    {
                        break;
                    }
                }

                Thread.Sleep(100);
            }

            return exitCode;
        }
        finally
        {
            sigterm?.Dispose();
            _pipeHost.Dispose();
            _control.Dispose();
            _session.Dispose();
            _registry.Delete();
            _job.Dispose();
            _mutex.Dispose();
            _trace?.Dispose();
            _log.Dispose();
        }
    }

    private static int? GetParentPid(string[] args)
    {
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];

            if (arg.StartsWith("--parent-pid=", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(arg["--parent-pid=".Length..], out int inline))
            {
                return inline > 0 ? inline : null;
            }

            if (string.Equals(arg, "--parent-pid", StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length &&
                int.TryParse(args[index + 1], out int value))
            {
                return value > 0 ? value : null;
            }
        }

        return null;
    }

    private static int ResolveDaemonIdleMs()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_DAEMON_IDLE_MS");

        if (int.TryParse(raw, out int value) && value >= 0)
        {
            return value;
        }

        return 5000;
    }
}
