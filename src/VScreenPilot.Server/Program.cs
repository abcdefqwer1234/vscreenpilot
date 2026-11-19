using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Cli;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Platform.Interop;
using VScreenPilot.Platform.Security;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
using VScreenPilot.Ipc;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Session;

namespace VScreenPilot;

internal static class Program
{
    private delegate bool ConsoleCtrlHandler(uint ctrlType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleCtrlHandler(
        ConsoleCtrlHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    private static SessionManager? _session;
    private static ControlServer? _control;
    private static PipeHost? _pipeHost;
    private static InstanceRegistry? _registry;
    private static ConsoleCtrlHandler? _ctrlHandler;

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
            PrintHelp();
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

        // 日志/环境摘要里的租约与客户端归属由 MCP 层提供。
        LogRing.ClientIdProvider = static () => SessionManager.CurrentClientId;
        SessionManager.LeaseHolderProvider = static () => McpServer.SharedScreenLease.HolderId;
        SessionManager.ScreenLeaseProvider = static () => McpServer.SharedScreenLease.Describe();
        ScreenLease.LogSink = log.Info;
        ScreenLease.ClientCwdProvider = static id => SessionManager.FindClientCwd(id);

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
            int cliExit = RunCliBeforeLock(
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
            int doctorExit = Doctor(log, allowScript, profile);
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
            _session = new SessionManager(log, stateStore, job, allowScript);
            return RunCli(cliCommand!, args, _session, log, job, mutex);
        }

        _registry = new InstanceRegistry(profile);
        InstanceRegistry.Entry entry = InstanceRegistry.Create(profile, ready: false);
        _registry.Write(entry);

        var appStateStore = new StateStore(log);
        var appJob = new JobObject();
        _session = new SessionManager(log, appStateStore, appJob, allowScript);
        trace = new TraceLog();

        if (allowScript)
        {
            log.Warn("run_script 已启用（--allow-script/VSP_ALLOW_SCRIPT），该工具可执行任意 PowerShell");
        }

        _control = new ControlServer(_session, log, _registry, profile, ShutdownFromControl);
        _control.Start();

        _pipeHost = new PipeHost(_session, log, trace, profile);
        _pipeHost.Start();

        entry.Ready = true;
        entry.DisplayDevice = _session.VirtualDisplay?.DeviceName ?? string.Empty;
        _registry.Write(entry);

        int? parentPid = GetParentPid(args);
        if (parentPid is not null)
        {
            StartParentWatcher(parentPid.Value, _session, log);
        }

        // 启动时后台预热虚拟屏：把不可避免的第一次黑屏挪到 OpenCode 启动阶段，
        // 而不是用户第一次调试时。失败则延迟到首次工具调用重试。
        // VSP_NO_WARMUP=1 用于无显示器的协议冒烟测试。
        if (Environment.GetEnvironmentVariable("VSP_NO_WARMUP") != "1")
        {
            _ = Task.Run(() =>
            {
                try
                {
                    _session.EnsureEnvironment();
                    _session.TrimIdleMemory("startup_warm");
                    entry.DisplayDevice = _session.VirtualDisplay?.DeviceName ?? string.Empty;
                    _registry?.Write(entry);
                    log.Info("虚拟屏预热完成");
                }
                catch (Exception exception)
                {
                    log.Warn($"虚拟屏预热失败，将在首次使用时重试: {exception.Message}");
                }
            });
        }

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            _pipeHost?.Dispose();
            _control?.Dispose();
            _session?.Dispose();
            _registry?.Delete();
            log.Dispose();
        };

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _pipeHost?.Dispose();
            _control?.Dispose();
            _session?.Dispose();
            _registry?.Delete();
            Environment.Exit(0);
        };

        _ctrlHandler = _ =>
        {
            _pipeHost?.Dispose();
            _control?.Dispose();
            _session?.Dispose();
            _registry?.Delete();
            return false;
        };
        SetConsoleCtrlHandler(_ctrlHandler, true);

        PosixSignalRegistration? sigterm = null;
        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                _pipeHost?.Dispose();
                _control?.Dispose();
                _session?.Dispose();
                _registry?.Delete();
                Environment.Exit(0);
            });
        }
        catch (PlatformNotSupportedException)
        {
            // Windows 上部分场景不支持，忽略。
        }

        try
        {
            var server = new McpServer(_session, log, trace!);
            int exitCode = server.RunAsync().GetAwaiter().GetResult();

            // 首个 MCP 客户端断开后，先等共享客户端；最后一个客户端断开后再进入空闲宽限。
            int idleMs = ResolveDaemonIdleMs();
            DateTime? idleSince = null;

            while (true)
            {
                int active = _pipeHost?.ActiveClients ?? 0;

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
            _pipeHost?.Dispose();
            _control?.Dispose();
            _session?.Dispose();
            _registry?.Delete();
            appJob.Dispose();
            mutex.Dispose();
            trace?.Dispose();
            log.Dispose();
        }
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine($"""
            VScreenPilot {BuildInfo.Version}

            用法:
              VScreenPilot [--allow-script] [--parent-pid <pid>] [--profile <name>]
              VScreenPilot --daemon [--profile <name>]
              VScreenPilot --cli env|snapshot|screen|status|stop [--force] [--profile <name>]
              VScreenPilot --doctor                                输出 JSON 诊断
              VScreenPilot --version                               输出版本
              VScreenPilot --help                                  输出本帮助

            选项:
              --allow-script            启用 run_script（默认关闭）
              --parent-pid <pid>        父进程退出时自动清理
              --profile <name>          多会话隔离：独立互斥体、状态文件、追踪与 artifact 目录
              --force                   配合 --cli stop：有活跃客户端时强制停止

            环境变量:
              VSP_DISPLAY_WIDTH        虚拟屏宽度（默认 1280，范围 800–3840）
              VSP_DISPLAY_HEIGHT       虚拟屏高度（默认 720，范围 600–2160）
              VSP_DISPLAY_REFRESH      虚拟屏刷新率（默认 60，范围 30–240）
              VSP_DISPLAY_OFFSET       虚拟屏相对主屏右侧的偏移（默认自动选空闲槽位）
              VSP_INPUT_AUTOFALLBACK=1 UIA/消息点击失败时允许自动降级 SendInput（默认关闭）
              VSP_ALLOW_SCRIPT=1        等价于 --allow-script
              VSP_ALLOW_UNSAFE_SCRIPT=1 绕过 run_script 危险命令黑名单（不是沙箱）
              VSP_APP_ALLOWLIST=dir1;dir2  仅允许启动这些目录下的 exe（默认允许全部）
              VSP_CLIENT_CWD=<dir>      覆盖客户端路径基准（默认 proxy cwd）
              VSP_SCREEN_LEASE_IDLE_MS  屏幕租约空闲毫秒数（默认 60000）
              VSP_DAEMON_IDLE_MS        最后一个客户端断开后的空闲宽限（默认 5000，0=立即退出）
              VSP_CLIENT_LIMIT          共享客户端上限（默认 16）
              VSP_SCREEN_LEASE_WAIT_MS  租约被占用时最多等待毫秒数（默认 0）
              VSP_TOTAL_BUFFER_MB       所有 MCP 连接输入总缓冲上限（默认 64）
              VSP_MAX_MESSAGE_MB        单条 MCP 消息上限（默认 16）

            显示设置在 daemon 启动时读取；修改后需先停止 daemon（--cli stop 或关闭客户端）再启动。
            """);
    }

    /// <summary>
    /// CLI 在抢锁/构造 SessionManager 之前分流：
    /// - 有 ctl：直接查询活跃实例；
    /// - 无 ctl 且互斥体空闲：env 走只读 helper，snapshot/screen 返回 -1 交给本地临时会话；
    /// - 无 ctl 且互斥体被占用：旧实例/异常实例，明确报错。
    /// </summary>
    private static int RunCliBeforeLock(
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

    private static int? RunProxy(string profile, bool allowScript)
    {
        // proxy 用独立文件，避免和 daemon 的 server-*.log 混在一起。
        using var proxyLog = new LogRing(clientLog: true);
        return StdioProxy.TryRun(profile, proxyLog, allowScript);
    }

    private static int? AttachToSpawnedDaemon(
        string profile, LogRing log, bool allowScript, Process child)
    {
        // 复用同一个 proxy LogRing：每次重试都新建会反复打开同一个文件、开新线程。
        using var proxyLog = new LogRing(clientLog: true);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadline)
        {
            if (child.HasExited)
            {
                return null;
            }

            int? exit = StdioProxy.TryRun(profile, proxyLog, allowScript);

            if (exit is not null)
            {
                return exit;
            }

            Thread.Sleep(150);
        }

        log.Warn("独立 daemon 启动后未能在 15s 内连接");
        return null;
    }

    private static int PrintControlResponse(JsonObject response)
    {
        if (response["ok"]?.GetValue<bool>() == true && response["result"] is JsonNode result)
        {
            // 与无实例时的本地 CLI 输出保持同一形状：所有成功结果都带 ok=true。
            JsonObject payload = result.DeepClone() as JsonObject ?? new JsonObject
            {
                ["result"] = result.DeepClone(),
            };

            payload["ok"] = true;
            PrintJson(payload);
            return 0;
        }

        PrintJson(response);
        return 1;
    }

    private static void PrintJson(JsonNode node)
    {
        Console.Out.WriteLine(node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsMutexHeld(string profile)
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
                    catch
                    {
                        // 忽略。
                    }

                    return false;
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                inaccessible = true;
            }
            catch
            {
                // 继续尝试下一个命名空间。
            }
        }

        return inaccessible;
    }

    private static void ReportInstanceBusy(string profile, string? error, LogRing log)
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
        catch
        {
            // 无控制台时忽略。
        }

        log.Warn($"实例已被占用: {error}");
    }

    private static void ShutdownFromControl()
    {
        _pipeHost?.Dispose();
        _control?.Dispose();
        _registry?.Delete();
        _session?.Dispose();
        Environment.Exit(0);
    }

    /// <summary>
    /// CLI 伴生入口：同一 exe 不经过 MCP 也能取环境/快照/截图。
    /// 编码代理用 CLI + skill 可以省掉工具 schema 的上下文成本。
    /// </summary>
    private static int RunCli(
        string command, string[] args, SessionManager session,
        LogRing log, JobObject job, MutexGuard mutex)
    {
        try
        {
            JsonObject result;

            switch (command.Trim().ToLowerInvariant())
            {
                case "env":
                    result = session.GetEnvironment(HasFlag(args, "--ensure"));
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
                    result = snapshot.Data;
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

    private static string? GetOption(string[] args, string option)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].StartsWith(option + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[index][(option.Length + 1)..];
            }

            if (string.Equals(args[index], option, StringComparison.OrdinalIgnoreCase) &&
                index + 1 < args.Length)
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static int ResolveDaemonIdleMs()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_DAEMON_IDLE_MS");

        if (int.TryParse(raw, out int value))
        {
            return Math.Clamp(value, 0, 600_000);
        }

        return 5000;
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

    /// <summary>
    /// 启动器被杀时也要走统一清理，避免虚拟屏和子进程残留。
    /// 这是进程事件监听，不是超时看门狗。
    /// </summary>
    private static void StartParentWatcher(int parentPid, SessionManager session, LogRing log)
    {
        var thread = new Thread(() =>
        {
            try
            {
                using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // 父进程在进入监听前已经退出。
            }
            catch (Exception exception)
            {
                log.Warn($"父进程监听失败 (pid={parentPid}): {exception.Message}");
                return;
            }

            log.Info($"父进程已退出 (pid={parentPid})，开始清理");

            if (_pipeHost is { ActiveClients: > 0 })
            {
                log.Warn("父进程退出但仍有共享客户端，继续作为 daemon 运行");
                return;
            }

            try
            {
                session.Dispose();
            }
            catch (Exception exception)
            {
                log.Error("父进程退出后的清理失败", exception);
            }

            Environment.Exit(0);
        })
        {
            IsBackground = true,
            Name = "ParentWatcher",
        };

        thread.Start();
    }

    private static int Doctor(LogRing log, bool allowScript, string profile)
    {
        var checks = new JsonArray();
        var result = new JsonObject
        {
            ["ok"] = true,
            ["version"] = BuildInfo.Version,
            ["profile"] = profile,
            ["platform"] = Environment.OSVersion.Platform.ToString(),
            ["os"] = RuntimeInformation.OSDescription,
            ["arch"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["elevated"] = Elevation.IsProcessElevated(),
            ["interactive"] = Environment.UserInteractive,
            ["run_script_enabled"] = allowScript,
            ["app_allowlist"] = ReadAppAllowlistForDoctor(),
            ["log_file"] = log.FilePath,
        };

        var registry = new InstanceRegistry(profile);
        result["registry"] = registry.Read()?.ToJson();

        JsonObject? ctlStatus = ControlClient.TryInvoke(
            profile,
            new JsonObject { ["vsp"] = 1, ["op"] = "status" },
            timeoutMs: 800,
            connectTimeoutMs: 150,
            retries: 1);

        if (ctlStatus?["ok"]?.GetValue<bool>() == true && ctlStatus["result"] is JsonObject status)
        {
            result["daemon"] = status.DeepClone();
        }

        var instances = new JsonArray();
        var seenPids = new HashSet<int>();

        void AddInstance(InstanceRegistry.Entry instance)
        {
            if (seenPids.Add(instance.Pid))
            {
                instances.Add((JsonNode)instance.ToJson());
            }
        }

        foreach (InstanceRegistry.Entry instance in InstanceRegistry.ListAll())
        {
            AddInstance(instance);
        }

        foreach (InstanceRegistry.Entry instance in InstanceRegistry.ListStateEntries())
        {
            AddInstance(instance);
        }

        result["instances"] = instances;

        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            result["process"] = new JsonObject
            {
                ["pid"] = Environment.ProcessId,
                ["working_set_mb"] = Math.Round(process.WorkingSet64 / 1048576.0, 1),
                ["handles"] = process.HandleCount,
            };
        }
        catch
        {
            // 诊断字段缺失不影响其余结果。
        }

        string driverStatus;
        try
        {
            driverStatus = VddDevice.QueryStatus().ToString();
        }
        catch (Exception exception)
        {
            driverStatus = $"Error: {exception.Message}";
        }

        var driver = new JsonObject { ["status"] = driverStatus };

        try
        {
            using var vdd = new VddDevice();
            if (vdd.TryOpen(out int openError))
            {
                int version = vdd.GetVersion();
                driver["open"] = true;
                driver["version"] = version >= 0
                    ? $"{((version >> 16) & 0xFFFF)}.{version & 0xFFFF}"
                    : null;
            }
            else
            {
                driver["open"] = false;
                driver["open_error"] = openError;
            }
        }
        catch (Exception exception)
        {
            driver["open"] = false;
            driver["open_error"] = exception.Message;
        }

        result["driver"] = driver;

        bool driverOk = string.Equals(driverStatus, "Ok", StringComparison.Ordinal);
        bool driverOpen = driver["open"]?.GetValue<bool>() == true;

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "driver.status",
            ["ok"] = driverOk,
            ["detail"] = driverStatus,
        });
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "driver.open",
            ["ok"] = driverOpen,
            ["detail"] = driverOpen ? driver["version"]?.GetValue<string>() : driver["open_error"]?.ToString(),
        });
        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "session.interactive",
            ["ok"] = Environment.UserInteractive,
            ["detail"] = Environment.UserInteractive ? "ok" : "non-interactive session",
        });

        string powerPointPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Microsoft Office", "root", "Office16", "POWERPNT.EXE");
        bool powerPointFound = File.Exists(powerPointPath);

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "office.powerpoint",
            ["ok"] = powerPointFound,
            ["detail"] = powerPointFound ? powerPointPath : "not found (Office 未安装或路径不同)",
        });

        bool artifactsWritable;
        string artifactsDetail;

        try
        {
            string probeDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VScreenPilot", "artifacts", "doctor");
            Directory.CreateDirectory(probeDirectory);
            string probeFile = Path.Combine(probeDirectory, $"probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probeFile, "ok");
            File.Delete(probeFile);
            artifactsWritable = true;
            artifactsDetail = probeDirectory;
        }
        catch (Exception exception)
        {
            artifactsWritable = false;
            artifactsDetail = exception.Message;
        }

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "artifacts.writable",
            ["ok"] = artifactsWritable,
            ["detail"] = artifactsDetail,
        });

        string? ownDevice = new StateStore(log).Load()?.DisplayDevice;
        DisplayInfo? parsec = !string.IsNullOrWhiteSpace(ownDevice)
            ? DisplayTopology.FindParsecDisplayByDevice(ownDevice)
            : string.Equals(profile, "default", StringComparison.Ordinal)
                ? DisplayTopology.FindParsecDisplay()
                : null;
        result["virtual_display"] = parsec is null
            ? new JsonObject { ["enabled"] = false }
            : new JsonObject
            {
                ["enabled"] = true,
                ["device"] = parsec.DeviceName,
                ["x"] = parsec.X,
                ["y"] = parsec.Y,
                ["width"] = parsec.Width,
                ["height"] = parsec.Height,
                ["dpi"] = parsec.Dpi,
            };

        DisplayInfo? primary = DisplayTopology.FindPrimary();
        result["primary"] = primary is null
            ? null
            : new JsonObject
            {
                ["device"] = primary.DeviceName,
                ["x"] = primary.X,
                ["y"] = primary.Y,
                ["width"] = primary.Width,
                ["height"] = primary.Height,
                ["dpi"] = primary.Dpi,
            };

        (int x, int y, int width, int height) = DisplayTopology.VirtualScreenBounds();
        result["virtual_screen"] = new JsonObject
        {
            ["x"] = x,
            ["y"] = y,
            ["width"] = width,
            ["height"] = height,
            ["monitors"] = NativeMethods.GetSystemMetrics(80),
        };

        StateStore stateStore = new(log);
        result["state_file"] = stateStore.FilePath;
        result["state"] = stateStore.Describe();
        result["artifacts"] = new JsonObject
        {
            ["root"] = SessionManager.ArtifactRootFor(profile),
            ["retention_days"] = SessionManager.ResolveArtifactRetentionDays(
                Environment.GetEnvironmentVariable(SessionManager.ArtifactRetentionEnv)),
        };

        checks.Add((JsonNode)new JsonObject
        {
            ["id"] = "virtual_display",
            ["ok"] = parsec is not null,
            ["detail"] = parsec is null ? "not mounted" : $"{(int)parsec.X},{(int)parsec.Y} {parsec.Width}x{parsec.Height}",
        });

        result["checks"] = checks;

        // 旧实现只看 driver.status，忽略了 driver.open=false，会误报 OK。
        bool ok = driverOk && driverOpen && Environment.UserInteractive;
        result["ok"] = ok;

        if (!ok)
        {
            result["hint"] = driverOk
                ? "驱动状态正常但设备打不开，或有检查项失败；查看 checks[]"
                : "驱动未就绪，请运行 scripts/install-driver.ps1";
        }

        Console.Out.WriteLine(result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return result["ok"]?.GetValue<bool>() == true ? 0 : 1;
    }

    private static JsonArray ReadAppAllowlistForDoctor()
    {
        var array = new JsonArray();
        string? raw = Environment.GetEnvironmentVariable("VSP_APP_ALLOWLIST");

        if (string.IsNullOrWhiteSpace(raw))
        {
            return array;
        }

        foreach (string part in raw.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            array.Add(part);
        }

        return array;
    }
}
