using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Json;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Tools;

/// <summary>System 组的 <c>get_log</c>：内存/文件日志筛选与错误摘要。</summary>
internal sealed class LogTool
{
    private readonly LogRing _log;
    private readonly Func<string> _clientId;

    public LogTool(LogRing log, Func<string> clientId)
    {
        _log = log;
        _clientId = clientId;
    }

    public ToolResponse GetLog(JsonElement arguments)
    {
        LogQuery query = ParseQuery(arguments);
        List<LogEntryView> memoryEntries = ReadMemory(query);

        IReadOnlyList<string> fileLines = query.Source is "file" or "both"
            ? _log.ReadFileTail(query.Tail)
            : Array.Empty<string>();

        (List<LogEntryView> fileEntries, LogEntry? lastFileError) = ReadFile(fileLines, query);
        LogEntry? lastError = FindLastError(lastFileError, query);

        var view = new LogView(
            query.Source,
            query.IncludeOther,
            query.Source == "file" ? fileEntries : memoryEntries,
            _log.FilePath,
            lastError is null ? null : ToLogView(lastError),
            query.Source is "file" or "both" ? fileEntries : null);

        return ToolResponseWriter.FromView(view);
    }

    private LogQuery ParseQuery(JsonElement arguments)
    {
        int tail = Math.Clamp(OptionalInt(arguments, "tail", 100), 1, 500);
        string source = (OptionalString(arguments, "source") ?? "memory").Trim().ToLowerInvariant();

        if (source is not ("memory" or "file" or "both"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 source: {source}（可选 memory/file/both）");
        }

        string? level = OptionalString(arguments, "level");
        ValidateLevel(level);

        return new LogQuery(
            tail,
            source,
            level,
            OptionalString(arguments, "contains"),
            OptionalBool(arguments, "include_other_clients", false),
            _clientId());
    }

    private static void ValidateLevel(string? level)
    {
        if (level is null)
        {
            return;
        }

        if (level.Trim().ToUpperInvariant() is "DEBUG" or "INFO" or "WARN" or "ERROR")
        {
            return;
        }

        throw new ToolException(
            "E_INVALID_ARG",
            $"未知 level: {level}（可选 DEBUG/INFO/WARN/ERROR）",
            new JsonObject { ["field"] = "level" },
            retryable: false,
            hint: "level 只接受 DEBUG/INFO/WARN/ERROR",
            stage: "validate");
    }

    private List<LogEntryView> ReadMemory(LogQuery query)
    {
        var entries = new List<LogEntryView>();

        foreach (LogEntry entry in _log.Tail(Math.Min(500, Math.Max(query.Tail, query.Tail * 4))))
        {
            if (query.Matches(entry))
            {
                entries.Add(ToLogView(entry));
            }
        }

        while (entries.Count > query.Tail)
        {
            entries.RemoveAt(0);
        }

        return entries;
    }

    private static (List<LogEntryView> Entries, LogEntry? LastError) ReadFile(
        IReadOnlyList<string> fileLines, LogQuery query)
    {
        var entries = new List<LogEntryView>();
        LogEntry? lastError = null;

        foreach (string line in fileLines)
        {
            ParseFileLine(line, query, entries, ref lastError);
        }

        return (entries, lastError);
    }

    private static void ParseFileLine(
        string line, LogQuery query, List<LogEntryView> entries, ref LogEntry? lastError)
    {
        LogEntry? parsed = LogRing.Parse(line);

        if (parsed is null)
        {
            if (!query.HasTextFilter)
            {
                entries.Add(new LogEntryView(null, "RAW", line, null));
            }

            return;
        }

        if (!query.Matches(parsed))
        {
            return;
        }

        entries.Add(ToLogView(parsed));

        if (string.Equals(parsed.Level, "ERROR", StringComparison.Ordinal))
        {
            lastError = parsed;
        }
    }

    private LogEntry? FindLastError(LogEntry? lastFileError, LogQuery query) =>
        _log.Tail(500)
            .Where(query.Visible)
            .LastOrDefault(entry => string.Equals(entry.Level, "ERROR", StringComparison.Ordinal))
        ?? lastFileError;

    private static LogEntryView ToLogView(LogEntry entry) =>
        new(
            entry.Time.ToString("O"),
            entry.Level,
            entry.Message,
            string.IsNullOrWhiteSpace(entry.ClientId) ? null : entry.ClientId);
}
