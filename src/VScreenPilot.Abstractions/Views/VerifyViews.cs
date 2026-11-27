using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>只有宽高的区域（assert_region 的颜色分支）。</summary>
public sealed record SizeView(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>`assert_region` 的输出视图；color 与 changed 两个分支共用。</summary>
public sealed record AssertRegionView(
    [property: JsonPropertyName("passed")] bool Passed,
    [property: JsonPropertyName("matched_ratio")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? MatchedRatio,
    [property: JsonPropertyName("actual_coverage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? ActualCoverage,
    [property: JsonPropertyName("coverage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Coverage,
    [property: JsonPropertyName("coverage_semantics")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? CoverageSemantics,
    [property: JsonPropertyName("tolerance")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Tolerance,
    [property: JsonPropertyName("expected")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Expected,
    [property: JsonPropertyName("average")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Average,
    [property: JsonPropertyName("region")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    SizeView? Region,
    [property: JsonPropertyName("changed_ratio")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? ChangedRatio,
    [property: JsonPropertyName("threshold")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Threshold,
    [property: JsonPropertyName("expect_changed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ExpectChanged,
    [property: JsonPropertyName("baseline_frame_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? BaselineFrameId,
    [property: JsonPropertyName("baseline")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    BoundsView? Baseline,
    [property: JsonPropertyName("clipped")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Clipped = null,
    [property: JsonPropertyName("requested_region")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    BoundsView? RequestedRegion = null);

/// <summary>
/// `wait_for` 的稳定外壳；`last` 是随条件类型变化的多态载荷（element/text/color/
/// window/pixel_diff/visual），保持 JsonObject 以免把六种形状压成一个大可选记录。
/// </summary>
public sealed record WaitForView(
    [property: JsonPropertyName("satisfied")] bool Satisfied,
    [property: JsonPropertyName("elapsed_ms")] long ElapsedMs,
    [property: JsonPropertyName("last")] JsonObject Last);

public sealed record VisualMatchView(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("center")] PointView Center,
    [property: JsonPropertyName("pixels")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Pixels,
    [property: JsonPropertyName("coverage")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Coverage,
    [property: JsonPropertyName("score")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Score,
    [property: JsonPropertyName("confidence")] double Confidence,
    [property: JsonPropertyName("scale")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Scale);

public sealed record TemplateSizeView(
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>`match_visual` 的输出视图；pixel/color/template 三个分支共用。</summary>
public sealed record MatchVisualView(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("elapsed_ms")] double ElapsedMs,
    [property: JsonPropertyName("point")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PointView? Point,
    [property: JsonPropertyName("color")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Color,
    [property: JsonPropertyName("rgb")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<int>? Rgb,
    [property: JsonPropertyName("region")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    OptionalBoundsView? Region,
    [property: JsonPropertyName("target")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Target,
    [property: JsonPropertyName("tolerance")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Tolerance,
    [property: JsonPropertyName("min_pixels")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MinPixels,
    [property: JsonPropertyName("max_area_ratio")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? MaxAreaRatio,
    [property: JsonPropertyName("exclude_border")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? ExcludeBorder,
    [property: JsonPropertyName("matched_pixels")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? MatchedPixels,
    [property: JsonPropertyName("count")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    int? Count,
    [property: JsonPropertyName("matches")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<VisualMatchView>? Matches,
    [property: JsonPropertyName("threshold")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? Threshold,
    [property: JsonPropertyName("template")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    TemplateSizeView? Template,
    [property: JsonPropertyName("ambiguous")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    bool? Ambiguous,
    [property: JsonPropertyName("score_margin")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    double? ScoreMargin);
