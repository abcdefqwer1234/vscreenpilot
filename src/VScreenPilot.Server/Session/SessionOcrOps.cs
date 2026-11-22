using System.Text.Json;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>OcrOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionOcrOps
{
    internal readonly SessionManager _host;

    public SessionOcrOps(SessionManager host) => _host = host;
    public ScreenTextView ReadScreenText(
        int? x, int? y, int? width, int? height, string? query, int maxLines) =>
        _host._screenText.ReadScreenText(x, y, width, height, query, maxLines);

    internal static bool OcrTextMatches(string haystack, string needle) =>
        ScreenTextService.OcrTextMatches(haystack, needle);

    public ClickTextView ClickText(
        string text, bool keepForeground, bool observe, string channel, bool requireUnique,
        int? regionX, int? regionY, int? regionWidth, int? regionHeight, bool fallbackInput) =>
        _host._screenText.ClickText(
            text, keepForeground, observe, channel, requireUnique,
            regionX, regionY, regionWidth, regionHeight, fallbackInput);
}
