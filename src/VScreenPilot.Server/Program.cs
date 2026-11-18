using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Core.Displays;
using VScreenPilot.Core.Interop;
using VScreenPilot.Infrastructure;
using VScreenPilot.Interop;
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
                           IsTruthy(Environment.GetEnvironmentVariable("VSP_ALLOW_SCRIPT"));

        // 多会话 profile：每个 profile 独立互斥体/状态/日志/追踪目录；输入所有权仍由前台决定。
        // profile 必须先解析，日志文件名才能带上 profile。
        string? profileFlag = GetOption(args, "--profile");

        if (!string.IsNullOrWhiteSpace(profileFlag))
        {
            Environment.SetEnvironmentVariable(Profile.EnvironmentVariable, profileFlag);
        }

        string profile = Profile.Resolve();
        var log = new LogRing();

        if (HasFlag(args, "--doctor"))
        {
            int doctorExit = Doctor(log, allowScript, profile);
            log.Dispose();
            return doctorExit;
        }

        var mutex = new MutexGuard();

        if (!mutex.TryAcquire(Profile.MutexName(profile), out string? error))
        {
            try
            {
                Console.Error.WriteLine($"VScreenPilot: {error}");
            }
            catch
            {
                // 无控制台时忽略。
            }

            mutex.Dispose();
            log.Dispose();
            return 3;
        }

        var stateStore = new StateStore(log);
        var job = new JobObject();
        _session = new SessionManager(log, stateStore, job, allowScript);

        if (allowScript)
        {
            log.Warn("run_script 已启用（--allow-script/VSP_ALLOW_SCRIPT），该工具可执行任意 PowerShell");
        }

        string? cliCommand = GetOption(args, "--cli");

        if (!string.IsNullOrWhiteSpace(cliCommand))
        {
            return RunCli(cliCommand, args, _session, log, job, mutex);
        }

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
            _session?.Dispose();
            log.Dispose();
        };

        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            _session?.Dispose();
            Environment.Exit(0);
        };

        _ctrlHandler = _ =>
        {
            _session?.Dispose();
            return false;
        };
        SetConsoleCtrlHandler(_ctrlHandler, true);

        PosixSignalRegistration? sigterm = null;
        try
        {
            sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                _session?.Dispose();
                Environment.Exit(0);
            });
        }
        catch (PlatformNotSupportedException)
        {
            // Windows 上部分场景不支持，忽略。
        }

        try
        {
            var server = new McpServer(_session, log);
            return server.RunAsync().GetAwaiter().GetResult();
        }
        finally
        {
            sigterm?.Dispose();
            _session?.Dispose();
            job.Dispose();
            mutex.Dispose();
            log.Dispose();
        }
    }

    private static void PrintHelp()
    {
        Console.Out.WriteLine($"""
            VScreenPilot {BuildInfo.Version}

            用法:
              VScreenPilot [--allow-script] [--parent-pid <pid>] [--profile <name>]
              VScreenPilot --cli env|snapshot|screen [--profile <name>]   输出纯 JSON（CLI 伴生入口）
              VScreenPilot --doctor                                输出 JSON 诊断
              VScreenPilot --version                               输出版本
              VScreenPilot --help                                  输出本帮助

            选项:
              --allow-script            启用 run_script（默认关闭）
              --parent-pid <pid>        父进程退出时自动清理
              --profile <name>          多会话隔离：独立互斥体、状态文件、追踪与 artifact 目录

            环境变量:
              VSP_ALLOW_SCRIPT=1        等价于 --allow-script
              VSP_APP_ALLOWLIST=dir1;dir2  仅允许启动这些目录下的 exe（默认允许全部）
            """);
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
                    capture.Meta["path"] = path;
                    result = capture.Meta;
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

    private static bool IsTruthy(string? value) =>
        value is not null &&
        (value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
         value.Equals("yes", StringComparison.OrdinalIgnoreCase));

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
            ["elevated"] = IsElevated(),
            ["interactive"] = Environment.UserInteractive,
            ["run_script_enabled"] = allowScript,
            ["app_allowlist"] = ReadAppAllowlistForDoctor(),
            ["log_file"] = log.FilePath,
        };

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

        DisplayInfo? parsec = DisplayTopology.FindParsecDisplay();
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

    private static bool IsElevated()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}
