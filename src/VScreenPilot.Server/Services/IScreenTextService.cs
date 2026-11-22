using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>屏幕 OCR 文本读取。</summary>
internal interface IScreenTextService
{
    ScreenTextView Read(ScreenTextRequest request);
}

internal sealed class ScreenTextToolService : IScreenTextService
{
    private readonly SessionManager _session;

    public ScreenTextToolService(SessionManager session) => _session = session;

    public ScreenTextView Read(ScreenTextRequest request) =>
        _session.OcrOps.ReadScreenText(
            request.X,
            request.Y,
            request.Width,
            request.Height,
            request.Query,
            request.MaxLines);
}
