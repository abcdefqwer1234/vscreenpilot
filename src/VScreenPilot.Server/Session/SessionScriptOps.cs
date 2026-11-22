using VScreenPilot.Scripting;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>
/// `run_script` 的会话侧接线：策略来自连接上下文与进程配置，执行在 <see cref="ScriptRunner"/>。
/// </summary>
/// <summary>ScriptOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionScriptOps
{
    internal readonly SessionManager _host;

    public SessionScriptOps(SessionManager host) => _host = host;

    public ScriptView RunScript(string code, int timeoutMs) =>
        _host.ScriptRunner.Run(
            code,
            timeoutMs,
            new ScriptRunContext(
                AllowScript: _host.Clients.Current?.AllowScript ?? _host._allowScript,
                AllowUnsafeScript: _host.Clients.Current is null
                    ? Server.Infrastructure.EnvFlags.IsTruthyLoose(
                        Environment.GetEnvironmentVariable("VSP_ALLOW_UNSAFE_SCRIPT"))
                    : _host.Clients.Current.AllowUnsafeScript,
                AssignToJob: process => _host._job.Assign(process),
                Warn: _host._log.Warn,
                Cancellation: _host.Cancellation));
}
