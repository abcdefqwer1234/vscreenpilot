using System.Text.Json;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>条件等待（element/text/color/window/pixel_diff）。</summary>
internal interface IWaitService
{
    WaitForView WaitFor(JsonElement condition, int timeoutMs, int? intervalMs);
}

internal sealed class WaitToolService : IWaitService
{
    private readonly SessionManager _session;

    public WaitToolService(SessionManager session) => _session = session;

    public WaitForView WaitFor(JsonElement condition, int timeoutMs, int? intervalMs) =>
        _session.WaitFor(condition, timeoutMs, intervalMs);
}
