using System.Text.Json;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public ScreenTextView ReadScreenText(
        int? x, int? y, int? width, int? height, string? query, int maxLines) =>
        _screenText.ReadScreenText(x, y, width, height, query, maxLines);

    internal static bool OcrTextMatches(string haystack, string needle) =>
        ScreenTextService.OcrTextMatches(haystack, needle);

    public ClickTextView ClickText(
        string text, bool keepForeground, bool observe, string channel, bool requireUnique,
        int? regionX, int? regionY, int? regionWidth, int? regionHeight, bool fallbackInput) =>
        _screenText.ClickText(
            text, keepForeground, observe, channel, requireUnique,
            regionX, regionY, regionWidth, regionHeight, fallbackInput);
}
