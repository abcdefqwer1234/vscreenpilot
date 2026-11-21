using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Session;

internal sealed partial class SessionManager
{
    public object FindElement(
        string? name,
        string? controlType,
        string? automationId,
        string nameMatch,
        int index,
        bool all,
        int maxResults,
        bool includeOffscreen,
        int? pid,
        string? role = null,
        string prefer = "auto") =>
        _finder.Find(
            name, controlType, automationId, nameMatch, index, all, maxResults,
            includeOffscreen, pid, role, prefer);

    public WaitForView WaitFor(JsonElement condition, int timeoutMs, int? intervalMs = null) =>
        _waits.Wait(condition, timeoutMs, intervalMs);

    public AssertRegionView AssertRegion(JsonElement region, JsonElement expectation) =>
        _regionVerifier.Assert(region, expectation);


    private TrackedApp? LastTrackedApp() => LastLiveTrackedApp();

    private HashSet<int> TrackedPids()
    {
        lock (_gate)
        {
            return _apps.Where(IsOwned).Select(app => app.Pid).ToHashSet();
        }
    }

}
