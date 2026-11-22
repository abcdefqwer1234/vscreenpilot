using VScreenPilot.Session;

namespace VScreenPilot.Services;

/// <summary>
/// 工具层与会话层之间唯一的客户端身份。除 5 个基础字段外，可选携带握手阶段构造的
/// 完整 <see cref="ClientContext"/>（allow_script / app_allowlist / proxy_pid / version 等），
/// 避免共享 daemon 下被压缩成残缺副本。
/// </summary>
internal sealed record ClientInfo(
    string Id,
    string? Cwd,
    IReadOnlyList<string> Roots,
    string Name = "local",
    bool IncludeAllApps = false)
{
    public ClientContext? Context { get; init; }
}
