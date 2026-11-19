using System.Diagnostics;
using System.Text;
using VScreenPilot.Cli;
using static VScreenPilot.Cli.CommandLineOptions;
using VScreenPilot.Diagnostics;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Interop;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Composition;
using VScreenPilot.Ipc;
using static VScreenPilot.Cli.ControlOutput;
using static VScreenPilot.Composition.InstanceGuard;
using static VScreenPilot.Ipc.ProxyRunner;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Session;

namespace VScreenPilot;

internal static class Program
{
    private static int Main(string[] args)
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DpiAwarenessContextPerMonitorAwareV2);

        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch
        {
            // 某些宿主没有可用的控制台编码，不影响 stdio。
        }

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            HelpText.Print();
            return 0;
        }

        if (HasFlag(args, "--version") || HasFlag(args, "-v"))
        {
            Console.Out.WriteLine($"VScreenPilot {BuildInfo.Version}");
            return 0;
        }

        bool allowScript = HasFlag(args, "--allow-script") ||
                           EnvFlags.IsTruthy(Environment.GetEnvironmentVariable("VSP_ALLOW_SCRIPT"));

        // 多会话 profile：每个 profile 独立互斥体/状态/日志/追踪目录；输入所有权仍由前台决定。
        // profile 必须先解析，日志文件名才能带上 profile。
        string? profileFlag = GetOption(args, "--profile");

        if (!string.IsNullOrWhiteSpace(profileFlag))
        {
            Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, profileFlag);
        }

        string profile = Profile.Resolve();
        var log = new LogRing();
        TraceLog? trace = null;

        string? cliCommand = GetOption(args, "--cli");
        bool cliNeedsLocalSession = false;
        bool cliRequested = args.Any(arg =>
            string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--cli=", StringComparison.OrdinalIgnoreCase));

        if (cliRequested &&
            (string.IsNullOrWhiteSpace(cliCommand) || cliCommand.StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("--cli 需要一个命令：env|snapshot|screen|status|stop");
            log.Dispose();
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(cliCommand))
        {
            int cliExit = ControlInvoker.Run(
                cliCommand, profile, log, HasFlag(args, "--force"),
                HasFlag(args, "--ensure"), HasFlag(args, "--all"), out cliNeedsLocalSession);

            if (cliExit >= 0)
            {
                log.Dispose();
                return cliExit;
            }
        }

        if (HasFlag(args, "--doctor"))
        {
            int doctorExit = DoctorReportBuilder.Run(log, allowScript, profile);
            log.Dispose();
            return doctorExit;
        }

        if (HasFlag(args, "--daemon"))
        {
            int daemonExit = DaemonHost.RunStandalone(profile, log, allowScript);
            log.Dispose();
            return daemonExit;
        }

        // 已有实例：直接做瘦代理，不构造 SessionManager。
        int? proxyExit = RunProxy(profile, allowScript);
        if (proxyExit is not null)
        {
            log.Dispose();
            return proxyExit.Value;
        }

        // 没有 daemon 时优先启动独立 daemon；--cli snapshot/screen 的本地会话不能触发 spawn。
        if (!cliNeedsLocalSession && !IsMutexHeld(profile))
        {
            using Process? spawned = DaemonSpawner.TryStart(profile, log);

            if (spawned is not null)
            {
                int? attached = AttachToSpawnedDaemon(profile, log, allowScript, spawned);
                if (attached is not null)
                {
                    log.Dispose();
                    return attached.Value;
                }

                try
                {
                    if (!spawned.HasExited)
                    {
                        spawned.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // 子进程可能已自行退出。
                }
            }
        }

        var mutex = new MutexGuard();

        if (!mutex.TryAcquire(Profile.MutexName(profile), out string? error))
        {
            // 第一个连接与 daemon 启动有竞态：先看 registry 判断是新实例启动还是旧实例。
            int attempts = 5;
            InstanceRegistry.Entry? known = new InstanceRegistry(profile).Read();

            if (known is not null &&
                string.Equals(known.Version, BuildInfo.Version, StringComparison.Ordinal))
            {
                attempts = 60;
            }

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                Thread.Sleep(200);
                proxyExit = RunProxy(profile, allowScript);

                if (proxyExit is not null)
                {
                    mutex.Dispose();
                    log.Dispose();
                    return proxyExit.Value;
                }
            }

            ReportInstanceBusy(profile, error, log);
            mutex.Dispose();
            log.Dispose();
            return 3;
        }

        if (cliNeedsLocalSession)
        {
            var stateStore = new StateStore(log);
            var job = new JobObject();
            var localSession = new SessionManager(log, stateStore, job, allowScript);
            McpServer.WireProviders(localSession, log);
            return LocalSessionInvoker.Run(cliCommand!, args, localSession, log, job, mutex);
        }

        var registry = new InstanceRegistry(profile);
        InstanceRegistry.Entry entry = InstanceRegistry.Create(profile, ready: false);
        registry.Write(entry);

        var appStateStore = new StateStore(log);
        var appJob = new JobObject();
        var session = new SessionManager(log, appStateStore, appJob, allowScript);
        McpServer.WireProviders(session, log);
        trace = new TraceLog();

        using var host = new ServerHost(
            session, log, trace, registry, appJob, mutex, allowScript);
        return host.Run(entry, args);
    }
}
