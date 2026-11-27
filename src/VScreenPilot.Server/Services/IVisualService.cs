using System.Text.Json;
using VScreenPilot.Session;
using VScreenPilot.Views;

namespace VScreenPilot.Services;

/// <summary>视觉断言：区域断言与模板/颜色匹配。</summary>
internal interface IVisualService
{
    AssertRegionView AssertRegion(JsonElement region, JsonElement expectation, bool clipOutOfBounds = false);
    MatchVisualView MatchVisual(JsonElement arguments);
}

internal sealed class VisualToolService : IVisualService
{
    private readonly SessionManager _session;

    public VisualToolService(SessionManager session) => _session = session;

    public AssertRegionView AssertRegion(
        JsonElement region, JsonElement expectation, bool clipOutOfBounds = false) =>
        _session.VerifyOps.AssertRegion(region, expectation, clipOutOfBounds);

    public MatchVisualView MatchVisual(JsonElement arguments) =>
        _session.VisualOps.MatchVisual(arguments);
}
