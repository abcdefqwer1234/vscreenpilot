using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>脚本执行（受 --allow-script 门控）。</summary>
internal interface IScriptService
{
    ScriptView Run(string code, int timeoutMs);
}

internal sealed class ScriptToolService : IScriptService
{
    private readonly SessionManager _session;

    public ScriptToolService(SessionManager session) => _session = session;

    public ScriptView Run(string code, int timeoutMs) => _session.ScriptOps.RunScript(code, timeoutMs);
}
