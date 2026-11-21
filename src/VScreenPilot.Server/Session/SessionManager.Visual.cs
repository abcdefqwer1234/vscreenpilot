using System.Text.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager : IVisualHost
{
    private readonly VisualMatchService _visual;

    public MatchVisualView MatchVisual(JsonElement arguments) => _visual.Match(arguments);

    DisplayInfo IVisualHost.EnsureDisplay() => EnsureEnvironment();

    (byte[] Bgra, int Width, int Height) IVisualHost.CaptureRegion(
        int x, int y, int width, int height) => CaptureRegion(x, y, width, height);

    string IVisualHost.ValidatePngPath(string path, string field) =>
        _paths.ValidatePngPath(path, field);

    bool IVisualHost.IsCancelled() => Cancellation.IsCancellationRequested;
}
