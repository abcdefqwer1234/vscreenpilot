using System.Diagnostics;
using System.Text;
using VScreenPilot.Views;
using VScreenPilot.Errors;

namespace VScreenPilot.Scripting;

/// <summary>一次脚本调用的策略与外部依赖，由会话在调用点装配。</summary>
internal sealed record ScriptRunContext(
    bool AllowScript,
    bool AllowUnsafeScript,
    Action<Process> AssignToJob,
    Action<string> Warn,
    CancellationToken Cancellation);

/// <summary>
/// PowerShell 脚本执行：临时目录 + UTF-8 BOM、危险命令黑名单、输出 64KB 截断、
/// 超时/取消时杀掉整棵进程树。行为与旧 `SessionManager.Script.cs` 一致。
/// </summary>
internal sealed class ScriptRunner
{
    private const int OutputCap = 64 * 1024;

    private static readonly string[] UnsafeScriptTokens =
    {
        "Invoke-WebRequest", "Invoke-RestMethod", "DownloadString", "DownloadFile",
        "Start-Process", "Start-Job", "New-Service", "sc.exe", "schtasks", "bcdedit",
        "reg add", "reg delete", "Format-Volume", "Clear-Disk", "Remove-Partition",
        "Set-MpPreference", "Add-MpPreference", "Disable-WindowsOptionalFeature",
        "Set-ExecutionPolicy", "New-LocalUser", "net user", "wmic", "bitsadmin",
        "certutil", "Remove-Item -Recurse", "rm -r", "Add-Type", "Reflection.Assembly",
    };

    public ScriptView Run(string code, int timeoutMs, ScriptRunContext context)
    {
        if (!context.AllowScript)
        {
            throw new ToolException(
                "E_DISABLED",
                "run_script 默认关闭；请以 --allow-script 或环境变量 VSP_ALLOW_SCRIPT=1 启动 VScreenPilot");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ToolException("E_INVALID_ARG", "code 不能为空");
        }

        EnsureScriptSafe(code, context.AllowUnsafeScript);
        timeoutMs = Math.Clamp(timeoutMs, 1000, 120_000);

        string root = Path.Combine(
            Path.GetTempPath(), "VScreenPilot", "scripts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "script.ps1");
        // Windows PowerShell 5.1 无 BOM 时按系统 ANSI 解码，中文脚本会乱码，必须写 BOM；
        // 同时强制控制台输出为 UTF-8，否则重定向的 stdout 会按 GBK 输出后被按 UTF-8 解码。
        string script = "$OutputEncoding = [Console]::OutputEncoding = [Text.Encoding]::UTF8\n" + code;
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        string powershell = Path.Combine(
            Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

        if (!File.Exists(powershell))
        {
            powershell = "powershell.exe";
        }

        var stopwatch = Stopwatch.StartNew();
        bool timedOut = false;
        int exitCode = -1;
        string stdout = string.Empty;
        string stderr = string.Empty;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = powershell,
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // 脚本也纳入作业对象：服务器被强杀时由系统回收，避免残留后台进程。
            context.AssignToJob(process);

            Task<string> stdoutTask = ReadCappedAsync(process.StandardOutput, OutputCap);
            Task<string> stderrTask = ReadCappedAsync(process.StandardError, OutputCap);

            bool cancelled = false;
            string killReason = string.Empty;

            while (true)
            {
                if (process.WaitForExit(100))
                {
                    exitCode = process.ExitCode;
                    break;
                }

                if (context.Cancellation.IsCancellationRequested)
                {
                    cancelled = true;
                    killReason = "请求已取消";
                    break;
                }

                if (stopwatch.ElapsedMilliseconds > timeoutMs)
                {
                    timedOut = true;
                    killReason = $"执行超时 ({timeoutMs}ms)";
                    break;
                }
            }

            if (cancelled || timedOut)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception exception)
                {
                    // 进程可能已退出。
                    System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
                }

                process.WaitForExit(2000);
            }

            stdout = ReadTask(stdoutTask);
            stderr = ReadTask(stderrTask);

            if (cancelled)
            {
                throw new ToolException("E_CANCELLED", killReason);
            }

            if (timedOut)
            {
                context.Warn($"run_script {killReason}");
            }
        }
        catch (ToolException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new ToolException("E_SCRIPT", $"执行脚本失败: {exception.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (Exception exception)
            {
                // 临时目录清理失败不影响结果。
                System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
            }
        }

        return new ScriptView(
            Stdout: Truncate(stdout, OutputCap),
            Stderr: Truncate(stderr, OutputCap),
            ExitCode: timedOut ? -1 : exitCode,
            TimedOut: timedOut,
            DurationMs: stopwatch.ElapsedMilliseconds,
            Policy: "blocklist",
            Sandbox: false);
    }

    /// <summary>
    /// run_script 默认关闭；启用后也只做危险命令黑名单 + 超时 + Job Object。
    /// 这不是安全边界，需要真隔离请用 VM/容器。
    /// </summary>
    private static void EnsureScriptSafe(string code, bool unsafeAllowed)
    {
        if (unsafeAllowed)
        {
            return;
        }

        foreach (string token in UnsafeScriptTokens)
        {
            if (code.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                throw new ToolException(
                    "E_DISABLED",
                    $"run_script 黑名单拒绝命令: {token}",
                    ToolDetails.ScriptToken(token),
                    retryable: false,
                    hint: "该能力不是沙箱；如确需执行，设置 VSP_ALLOW_UNSAFE_SCRIPT=1 并自行承担风险",
                    stage: "script");
            }
        }
    }

    private static string ReadTask(Task<string> task)
    {
        try
        {
            return task.Wait(2000) ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>读取 stdout/stderr，超过上限的内容直接丢弃，避免脚本刷屏撑爆内存。</summary>
    private static Task<string> ReadCappedAsync(StreamReader reader, int cap) =>
        Task.Run(() =>
        {
            var builder = new StringBuilder(cap);
            var buffer = new char[4096];
            bool truncated = false;
            int read;

            try
            {
                while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (builder.Length < cap)
                    {
                        int take = Math.Min(read, cap - builder.Length);
                        builder.Append(buffer, 0, take);
                        truncated |= take < read;
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }
            catch (Exception exception)
            {
                // 进程被杀时读取可能中断，保留已读内容。
                System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
            }

            if (truncated)
            {
                builder.Append("\n...[truncated]");
            }

            return builder.ToString();
        });

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "\n...[truncated]";
}
