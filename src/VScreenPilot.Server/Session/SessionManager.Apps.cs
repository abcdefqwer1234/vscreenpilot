using System.Text.Json.Nodes;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public JsonObject LaunchOrPlaceApp(
        string? path,
        string? windowTitle,
        string? arguments,
        int waitMs,
        bool newInstance,
        bool ensureDocument = true,
        int? pid = null) =>
        _launcher.Launch(path, windowTitle, arguments, waitMs, newInstance, ensureDocument, pid);
}
