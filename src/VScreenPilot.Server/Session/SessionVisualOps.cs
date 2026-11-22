using System.Text.Json;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Views;

namespace VScreenPilot.Session;

/// <summary>VisualOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionVisualOps
{
    internal readonly SessionManager _host;

    public SessionVisualOps(SessionManager host) => _host = host;

    public MatchVisualView MatchVisual(JsonElement arguments) => _host.VisualMatch.Match(arguments);
}
