using System.Diagnostics;
using VScreenPilot.Infrastructure;
using VScreenPilot.Session;

namespace VScreenPilot.Ipc;

/// <summary>
/// 独立 daemon 模式：不占用 MCP stdio，只提供 mcp/ctl 管道。
/// 由首个客户端 spawn，或在 Job 不允许 breakaway 时由首个进程内嵌回退。
/// </summary>
internal sealed class DaemonHost : IDisposable
{
    private readonly SessionManager _session;
    private readonly LogRing _log;
    private readonly InstanceRegistry _registry;
    private readonly ControlServer _control;
    private readonly PipeHost _pipeHost;
    private readonly TraceLog _trace;
    private readonly JobObject _job;
    private readonly MutexGuard _mutex;
    private readonly ManualResetEventSlim _stop;
    private int _disposed;

    private DaemonHost(
        SessionManager session,
        LogRing log,
        InstanceRegistry registry,
        ControlServer control,
        PipeHost pipeHost,
        TraceLog trace,
        JobObject job,
        MutexGuard mutex,
        ManualResetEventSlim stop)
    {
        _session = session;
        _log = log;
        _registry = registry;
        _control = control;
        _pipeHost = pipeHost;
        _trace = trace;
        _job = job;
        _mutex = mutex;
        _stop = stop;
    }

    public static int RunStandalone(string profile, LogRing log, bool allowScript)
    {
        var mutex = new MutexGuard();

        if (!mutex.TryAcquire(Profile.MutexName(profile), out string? error))
        {
            log.Warn($"独立 daemon 无法启动: {error}");
            Console.Error.WriteLine($"VScreenPilot: 独立 daemon 无法启动 ({error})");
            mutex.Dispose();
            return 3;
        }

        var job = new JobObject();
        var stateStore = new StateStore(log);
        var session = new SessionManager(log, stateStore, job, allowScript);
        var trace = new TraceLog();
        var registry = new InstanceRegistry(profile);
        InstanceRegistry.Entry entry = InstanceRegistry.Create(profile, ready: false);
        registry.Write(entry);

        var stop = new ManualResetEventSlim(false);
        var control = new ControlServer(session, log, registry, profile, stop.Set);
        var pipeHost = new PipeHost(session, log, trace, profile);
        var host = new DaemonHost(
            session, log, registry, control, pipeHost, trace, job, mutex, stop);

        try
        {
            return host.Run(profile, entry, allowScript);
        }
        finally
        {
            host.Dispose();
        }
    }

    private int Run(string profile, InstanceRegistry.Entry entry, bool allowScript)
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Dispose();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _stop.Set();
        };

        if (allowScript)
        {
            _log.Warn("独立 daemon 已启用 run_script（--allow-script/VSP_ALLOW_SCRIPT）");
        }

        _control.Start();
        _pipeHost.Start();
        entry.Ready = true;
        entry.DisplayDevice = _session.VirtualDisplay?.DeviceName ?? string.Empty;
        _registry.Write(entry);

        if (Environment.GetEnvironmentVariable("VSP_NO_WARMUP") != "1")
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _session.EnsureEnvironment();
                    _session.TrimIdleMemory("daemon_warm");

                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        entry.DisplayDevice = _session.VirtualDisplay?.DeviceName ?? string.Empty;
                        _registry.Write(entry);
                        _log.Info("独立 daemon 虚拟屏预热完成");
                    }
                }
                catch (Exception exception)
                {
                    _log.Warn($"独立 daemon 预热失败，将在首次使用时重试: {exception.Message}");
                }
            });
        }

        _log.Info($"独立 daemon 已启动 profile={profile} clients={SessionManager.ActiveClientCount}");

        int idleMs = ResolveIdleMs();
        bool hadClient = false;
        DateTime? idleSince = DateTime.UtcNow;

        while (!_stop.Wait(100))
        {
            int active = _pipeHost.ActiveClients;

            if (active > 0)
            {
                hadClient = true;
                idleSince = null;
                continue;
            }

            idleSince ??= DateTime.UtcNow;

            // 首次启动（无论自动 spawn 还是手动 --daemon）都给客户端留出 10s 连接窗口；
            // 一旦有过客户端，就严格按 VSP_DAEMON_IDLE_MS 退出。
            int graceMs = hadClient ? idleMs : Math.Max(idleMs, 10_000);

            if (graceMs == 0 ||
                DateTime.UtcNow - idleSince.Value >= TimeSpan.FromMilliseconds(graceMs))
            {
                break;
            }
        }

        _log.Info("独立 daemon 进入退出流程");
        return 0;
    }

    private static int ResolveIdleMs()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_DAEMON_IDLE_MS");

        return int.TryParse(raw, out int value) ? Math.Clamp(value, 0, 600_000) : 5000;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _stop.Set();
            _pipeHost.Dispose();
            _control.Dispose();
            _session.Dispose();
            // registry 最后删：避免新客户端在旧 daemon 还在清理窗口时误判“无实例”
            // 并 spawn 一个抢不到互斥体的新 daemon。
            _registry.Delete();
            _trace.Dispose();
        }
        catch (Exception exception)
        {
            _log.Warn($"独立 daemon 清理失败: {exception.Message}");
        }
        finally
        {
            _job.Dispose();
            _mutex.Dispose();
            // ManualResetEventSlim 不持有需要立即释放的非托管资源；
            // 不在 ProcessExit 并发路径 Dispose，避免 Set/Wait 竞争。
        }
    }
}
