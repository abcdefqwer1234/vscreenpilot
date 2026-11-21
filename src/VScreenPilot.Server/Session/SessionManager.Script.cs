using VScreenPilot.Scripting;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>
/// `run_script` 的会话侧接线：策略来自连接上下文与进程配置，执行在 <see cref="ScriptRunner"/>。
/// </summary>
internal sealed partial class SessionManager
{
    private readonly ScriptRunner _scriptRunner = new();

    public ScriptView RunScript(string code, int timeoutMs) =>
        _scriptRunner.Run(
            code,
            timeoutMs,
            new ScriptRunContext(
                AllowScript: CurrentClient?.AllowScript ?? _allowScript,
                AllowUnsafeScript: CurrentClient is null
                    ? Infrastructure.EnvFlags.IsTruthyLoose(
                        Environment.GetEnvironmentVariable("VSP_ALLOW_UNSAFE_SCRIPT"))
                    : CurrentClient.AllowUnsafeScript,
                AssignToJob: process => _job.Assign(process),
                Warn: _log.Warn,
                Cancellation: Cancellation));
}
