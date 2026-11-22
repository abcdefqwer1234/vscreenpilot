using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VScreenPilot.Apps;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Interop;
using VScreenPilot.Json;
using VScreenPilot.Ui;
using VScreenPilot.Uia;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;

namespace VScreenPilot.Session;

/// <summary>VerifyOps：从 SessionManager 拆出的操作组，只持有宿主引用。</summary>
internal sealed class SessionVerifyOps
{
    internal readonly SessionManager _host;

    public SessionVerifyOps(SessionManager host) => _host = host;
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
        _host._finder.Find(
            name, controlType, automationId, nameMatch, index, all, maxResults,
            includeOffscreen, pid, role, prefer);

    public WaitForView WaitFor(JsonElement condition, int timeoutMs, int? intervalMs = null) =>
        _host._waits.Wait(condition, timeoutMs, intervalMs);

    public AssertRegionView AssertRegion(JsonElement region, JsonElement expectation) =>
        _host._regionVerifier.Assert(region, expectation);


    internal TrackedApp? LastTrackedApp() => _host.LastLiveTrackedApp();

    internal HashSet<int> TrackedPids()
    {
        lock (_host._gate)
        {
            return _host._apps.Where(_host.ClientOps.IsOwned).Select(app => app.Pid).ToHashSet();
        }
    }
}
