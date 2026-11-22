using System.Text.Json;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>OcrOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionOcrOps
{
    internal readonly SessionManager _host;

    public SessionOcrOps(SessionManager host) => _host = host;
    public ScreenTextView ReadScreenText(OcrReadRequest request) =>
        _host._screenText.ReadScreenText(request);

    internal static bool OcrTextMatches(string haystack, string needle) =>
        ScreenTextService.OcrTextMatches(haystack, needle);

    public ClickTextView ClickText(TextClickRequest request) =>
        _host._screenText.ClickText(request);
}
