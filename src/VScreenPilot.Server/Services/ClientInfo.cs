namespace VScreenPilot.Services;

/// <summary>工具层与会话层之间唯一的客户端身份；不携带 SessionManager 或完整 ClientContext。</summary>
internal sealed record ClientInfo(
    string Id,
    string? Cwd,
    IReadOnlyList<string> Roots,
    string Name = "local",
    bool IncludeAllApps = false);
