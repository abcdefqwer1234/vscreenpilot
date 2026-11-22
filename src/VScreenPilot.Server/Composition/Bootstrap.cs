using System.Diagnostics;
using System.Text;
using VScreenPilot.Cli;
using VScreenPilot.Diagnostics;
using VScreenPilot.Infrastructure;
using VScreenPilot.Ipc;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Interop;
using VScreenPilot.Platform.Logging;
using VScreenPilot.Platform.Processes;
using VScreenPilot.Platform.Security;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Session;
using static VScreenPilot.Cli.CommandLineOptions;
using static VScreenPilot.Composition.InstanceGuard;
using static VScreenPilot.Ipc.ProxyRunner;

namespace VScreenPilot.Composition;

/// <summary>进程启动编排：立即命令、CLI/doctor/daemon、实例互斥、本地会话与 host。</summary>
internal static class Bootstrap
{
    public static int Run(string[] args)
    {
        ConfigureConsole();

        if (TryImmediate(args, out int immediate))
        {
            return immediate;
        }

        bool allowScript = HasFlag(args, "--allow-script") ||
                           EnvFlags.IsTruthy(Environment.GetEnvironmentVariable("VSP_ALLOW_SCRIPT"));
        string? profileFlag = GetOption(args, "--profile");

        if (!string.IsNullOrWhiteSpace(profileFlag))
        {
            Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, profileFlag);
        }

        string profile = Profile.Resolve();
        using var log = new LogRing();
        LogSink.Configure(Path.Combine(
            Path.GetDirectoryName(log.FilePath) ?? AppContext.BaseDirectory,
            $"trace-{profile}.log"));

        string? cliCommand = GetOption(args, "--cli");
        bool cliRequested = args.Any(arg =>
            string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--cli=", StringComparison.OrdinalIgnoreCase));

        if (cliRequested &&
            (string.IsNullOrWhiteSpace(cliCommand) || cliCommand.StartsWith("--", StringComparison.Ordinal)))
        {
            Console.Error.WriteLine("--cli 需要一个命令：env|snapshot|screen|status|stop");
            return 2;
        }

        int? cliExit = TryRunCli(args, cliCommand, profile, log, out bool cliNeedsLocalSession);
        if (cliExit is not null)
        {
            return cliExit.Value;
        }

        if (HasFlag(args, "--doctor"))
        {
            return DoctorReportBuilder.Run(log, allowScript, profile);
        }

        if (HasFlag(args, "--daemon"))
        {
            return DaemonHost.RunStandalone(profile, log, allowScript);
        }

        int? proxyExit = RunProxy(profile, allowScript);
        if (proxyExit is not null)
        {
            return proxyExit.Value;
        }

        int? spawnedExit = TrySpawnDaemon(profile, log, allowScript, cliNeedsLocalSession);
        if (spawnedExit is not null)
        {
            return spawnedExit.Value;
        }

        using var mutex = new MutexGuard();
        int? busyExit = AcquireMutexOrWait(mutex, profile, log, allowScript);
        if (busyExit is not null)
        {
            return busyExit.Value;
        }

        return cliNeedsLocalSession
            ? RunLocalSession(args, cliCommand!, log, mutex, allowScript)
            : RunHost(args, profile, log, mutex, allowScript);
    }

    private static bool TryImmediate(string[] args, out int exit)
    {
        exit = 0;

        if (HasFlag(args, "--help") || HasFlag(args, "-h"))
        {
            HelpText.Print();
            return true;
        }

        if (HasFlag(args, "--version") || HasFlag(args, "-v"))
        {
            Console.Out.WriteLine($"VScreenPilot {BuildInfo.Version}");
            return true;
        }

        return false;
    }

    private static void ConfigureConsole()
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DpiAwarenessContextPerMonitorAwareV2);

        try
        {
            Console.OutputEncoding = new UTF8Encoding(false);
        }
        catch (Exception exception)
        {
            // 某些宿主没有可用的控制台编码，不影响 stdio。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }
    }

    private static int? TryRunCli(
        string[] args, string? cliCommand, string profile, LogRing log, out bool cliNeedsLocalSession)
    {
        cliNeedsLocalSession = false;

        if (string.IsNullOrWhiteSpace(cliCommand))
        {
            return null;
        }

        int cliExit = ControlInvoker.Run(
            new ControlCommand(
                cliCommand,
                profile,
                HasFlag(args, "--force"),
                HasFlag(args, "--ensure"),
                HasFlag(args, "--all")),
            log,
            out cliNeedsLocalSession);

        return cliExit >= 0 ? cliExit : null;
    }

    /// <summary>没有 daemon 时优先启动独立 daemon；-cli snapshot/screen 的本地会话不能触发 spawn。</summary>
    private static int? TrySpawnDaemon(
        string profile, LogRing log, bool allowScript, bool cliNeedsLocalSession)
    {
        if (cliNeedsLocalSession || IsMutexHeld(profile))
        {
            return null;
        }

        using Process? spawned = DaemonSpawner.TryStart(profile, log);

        if (spawned is null)
        {
            return null;
        }

        int? attached = AttachToSpawnedDaemon(profile, log, allowScript, spawned);
        if (attached is not null)
        {
            return attached;
        }

        try
        {
            if (!spawned.HasExited)
            {
                spawned.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            // 子进程可能已自行退出。
            LogSink.Debug($"忽略异常: {exception.Message}");
        }

        return null;
    }

    /// <summary>互斥体被占用时等待 daemon 就绪；拿到控制管道就返回代理退出码。</summary>
    private static int? AcquireMutexOrWait(MutexGuard mutex, string profile, LogRing log, bool allowScript)
    {
        if (mutex.TryAcquire(Profile.MutexName(profile), out string? error))
        {
            return null;
        }

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
            int? proxyExit = RunProxy(profile, allowScript);

            if (proxyExit is not null)
            {
                return proxyExit;
            }
        }

        ReportInstanceBusy(profile, error, log);
        return 3;
    }

    private static int RunLocalSession(
        string[] args, string cliCommand, LogRing log, MutexGuard mutex, bool allowScript)
    {
        var stateStore = new StateStore(log);
        var job = new JobObject();
        var localSession = new SessionManager(log, stateStore, job, allowScript);
        SessionWiring.Wire(localSession, log);
        return LocalSessionInvoker.Run(cliCommand, args, new SessionFacade(localSession), log, job, mutex);
    }

    private static int RunHost(
        string[] args, string profile, LogRing log, MutexGuard mutex, bool allowScript)
    {
        var registry = new InstanceRegistry(profile);
        InstanceRegistry.Entry entry = InstanceRegistry.Create(profile, ready: false);
        registry.Write(entry);

        var appStateStore = new StateStore(log);
        var appJob = new JobObject();
        var session = new SessionManager(log, appStateStore, appJob, allowScript);
        SessionWiring.Wire(session, log);
        var trace = new TraceLog();

        using var host = new ServerHost(
            new ServerHostResources(session, log, trace, registry, appJob, mutex),
            new ServerHostSettings(allowScript));
        return host.Run(entry, args);
    }
}
