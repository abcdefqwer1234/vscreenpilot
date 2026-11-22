using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Tools;

/// <summary>get_log 的筛选条件，供内存与文件日志共用同一套匹配规则。</summary>
internal sealed record LogQuery(
    int Tail,
    string Source,
    string? Level,
    string? Contains,
    bool IncludeOther,
    string CurrentClient)
{
    public bool HasTextFilter => !string.IsNullOrWhiteSpace(Level) || !string.IsNullOrWhiteSpace(Contains);

    public bool Visible(LogEntry entry) =>
        IncludeOther ||
        entry.ClientId is null ||
        string.Equals(entry.ClientId, CurrentClient, StringComparison.Ordinal);

    public bool Matches(LogEntry entry) =>
        Visible(entry) &&
        (string.IsNullOrWhiteSpace(Level) ||
         entry.Level.Equals(Level.Trim(), StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(Contains) ||
         entry.Message.Contains(Contains, StringComparison.OrdinalIgnoreCase));
}
