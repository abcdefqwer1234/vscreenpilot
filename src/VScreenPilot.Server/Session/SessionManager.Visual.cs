using System.Text.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    private readonly VisualMatchService _visual;

    public MatchVisualView MatchVisual(JsonElement arguments) => _visual.Match(arguments);
}
