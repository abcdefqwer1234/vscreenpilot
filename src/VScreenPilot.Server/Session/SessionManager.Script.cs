using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Mcp;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    private const int OutputCap = 64 * 1024;

    public JsonObject RunScript(string code, int timeoutMs)
    {
        if (!_allowScript)
        {
            throw new ToolException(
                "E_DISABLED",
                "run_script 默认关闭；请以 --allow-script 或环境变量 VSP_ALLOW_SCRIPT=1 启动 VScreenPilot");
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ToolException("E_INVALID_ARG", "code 不能为空");
        }

        timeoutMs = Math.Clamp(timeoutMs, 1000, 120_000);

        string root = Path.Combine(
            Path.GetTempPath(), "VScreenPilot", "scripts", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scriptPath = Path.Combine(root, "script.ps1");
        // Windows PowerShell 5.1 无 BOM 时按系统 ANSI 解码，中文脚本会乱码，必须写 BOM。
        File.WriteAllText(scriptPath, code, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

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
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // 脚本也纳入作业对象：服务器被强杀时由系统回收，避免残留后台进程。
            // 失败通常是因为进程已被宿主作业接管，属正常情况，不记警告。
            _job.Assign(process);

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

                if (Cancellation.IsCancellationRequested)
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
                catch
                {
                    // 进程可能已退出。
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
                _log.Warn($"run_script {killReason}");
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
            catch
            {
                // 临时目录清理失败不影响结果。
            }
        }

        return new JsonObject
        {
            ["stdout"] = Truncate(stdout, 64 * 1024),
            ["stderr"] = Truncate(stderr, 64 * 1024),
            ["exit_code"] = timedOut ? -1 : exitCode,
            ["timed_out"] = timedOut,
            ["duration_ms"] = stopwatch.ElapsedMilliseconds,
        };
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
            catch
            {
                // 进程被杀时读取可能中断，保留已读内容。
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
