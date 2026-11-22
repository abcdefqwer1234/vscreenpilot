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
        int tail = Math.Clamp(OptionalInt(arguments, "tail", 100), 1, 500);
        string source = (OptionalString(arguments, "source") ?? "memory").Trim().ToLowerInvariant();

        if (source is not ("memory" or "file" or "both"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 source: {source}（可选 memory/file/both）");
        }

        string? level = OptionalString(arguments, "level");

        if (level is not null &&
            level.Trim().ToUpperInvariant() is not ("DEBUG" or "INFO" or "WARN" or "ERROR"))
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"未知 level: {level}（可选 DEBUG/INFO/WARN/ERROR）",
                new JsonObject { ["field"] = "level" },
                retryable: false,
                hint: "level 只接受 DEBUG/INFO/WARN/ERROR",
                stage: "validate");
        }

        string? contains = OptionalString(arguments, "contains");
        bool includeOther = OptionalBool(arguments, "include_other_clients", false);
        string currentClient = _clientId();

        bool Visible(LogEntry entry) =>
            includeOther ||
            entry.ClientId is null ||
            string.Equals(entry.ClientId, currentClient, StringComparison.Ordinal);

        bool Matches(LogEntry entry) =>
            Visible(entry) &&
            (string.IsNullOrWhiteSpace(level) ||
             entry.Level.Equals(level.Trim(), StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrWhiteSpace(contains) ||
             entry.Message.Contains(contains, StringComparison.OrdinalIgnoreCase));

        var memoryEntries = new List<LogEntryView>();

        foreach (LogEntry entry in _log.Tail(Math.Min(500, Math.Max(tail, tail * 4))))
        {
            if (Matches(entry))
            {
                memoryEntries.Add(ToLogView(entry));
            }
        }

        while (memoryEntries.Count > tail)
        {
            memoryEntries.RemoveAt(0);
        }

        IReadOnlyList<string> fileLines = source is "file" or "both"
            ? _log.ReadFileTail(tail)
            : Array.Empty<string>();

        var fileEntries = new List<LogEntryView>();
        LogEntry? lastFileError = null;

        foreach (string line in fileLines)
        {
            LogEntry? parsed = LogRing.Parse(line);

            if (parsed is null)
            {
                if (string.IsNullOrWhiteSpace(level) && string.IsNullOrWhiteSpace(contains))
                {
                    fileEntries.Add(new LogEntryView(null, "RAW", line, null));
                }
                continue;
            }

            if (!Matches(parsed))
            {
                continue;
            }

            fileEntries.Add(ToLogView(parsed));

            if (string.Equals(parsed.Level, "ERROR", StringComparison.Ordinal))
            {
                lastFileError = parsed;
            }
        }

        LogEntry? lastError = _log.Tail(500)
            .Where(Visible)
            .LastOrDefault(entry => string.Equals(entry.Level, "ERROR", StringComparison.Ordinal))
            ?? lastFileError;

        var view = new LogView(
            source,
            includeOther,
            source == "file" ? fileEntries : memoryEntries,
            _log.FilePath,
            lastError is null ? null : ToLogView(lastError),
            source is "file" or "both" ? fileEntries : null);

        return ToolResponseWriter.FromView(view);
    }

    private static LogEntryView ToLogView(LogEntry entry) =>
        new(
            entry.Time.ToString("O"),
            entry.Level,
            entry.Message,
            string.IsNullOrWhiteSpace(entry.ClientId) ? null : entry.ClientId);
}
