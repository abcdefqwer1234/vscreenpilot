using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Displays;
using VScreenPilot.Views;
using static VScreenPilot.Json.JsonArgReader;
using VScreenPilot.Errors;
using VScreenPilot.Json;

namespace VScreenPilot.Session;

/// <summary>采集到的 BGRA 图像。</summary>
internal readonly record struct CapturedImage(byte[] Bgra, int Width, int Height);

/// <summary>assert_region 所需的最小宿主接口。</summary>
internal sealed record RegionVerifierContext(
    Func<DisplayInfo> EnsureDisplay,
    Func<int, int, int, int, (byte[] Bgra, int Width, int Height)> CaptureRegion,
    Func<string, FrameSnapshot?> FindFrame,
    Func<int, int, int, int, FrameSnapshot?> FindLatestFrame,
    Func<FrameSnapshot?> LatestFrame,
    Func<IReadOnlyList<FrameSnapshot>> Frames);

/// <summary>assert_region：颜色覆盖率与基线变化两种断言。</summary>
internal sealed class RegionVerifier
{
    private readonly RegionVerifierContext _host;

    public RegionVerifier(RegionVerifierContext host) => _host = host;

    public AssertRegionView Assert(
        JsonElement region, JsonElement expectation, bool clipOutOfBounds = false)
    {
        DisplayInfo display = _host.EnsureDisplay();
        (int requestedX, int requestedY, int requestedWidth, int requestedHeight) = ParseRegion(region);
        int x = requestedX;
        int y = requestedY;
        int width = requestedWidth;
        int height = requestedHeight;
        bool clipped = false;

        EnsureExpectation(expectation);

        if (clipOutOfBounds)
        {
            var clamped = VisualSupport.ClipRegionToDisplay(
                display, requestedX, requestedY, requestedWidth, requestedHeight, "assert_region");
            x = clamped.X;
            y = clamped.Y;
            width = clamped.Width;
            height = clamped.Height;
            clipped = clamped.Clipped;
        }
        else
        {
            VisualSupport.RequireRegionInDisplay(
                display, requestedX, requestedY, requestedWidth, requestedHeight, "assert_region");
        }

        (byte[] bgra, int capturedWidth, int capturedHeight) = _host.CaptureRegion(x, y, width, height);
        AssertRegionView view = AssertExpectation(bgra, capturedWidth, capturedHeight, x, y, expectation);

        return clipped
            ? view with
            {
                Clipped = true,
                RequestedRegion = new BoundsView(requestedX, requestedY, requestedWidth, requestedHeight),
            }
            : view;
    }

    private static (int X, int Y, int Width, int Height) ParseRegion(JsonElement region)
    {
        (int x, int y, int width, int height) = VisualSupport.ParseRegion(region);

        if (width > 0 && height > 0 && x >= int.MinValue + 1 && y >= int.MinValue + 1)
        {
            return (x, y, width, height);
        }

        throw new ToolException(
            "E_INVALID_ARG",
            "assert_region 的 region 必须包含正整数 width/height",
            new JsonObject
            {
                ["field"] = "region",
                ["x"] = x,
                ["y"] = y,
                ["width"] = width,
                ["height"] = height,
            },
            retryable: false,
            hint: "传 {x,y,width,height}，width/height ≥ 1",
            stage: "validate");
    }

    private static void EnsureExpectation(JsonElement expectation)
    {
        // expectation 至少要有一个断言，否则不要浪费一次截图。
        bool hasExpectation =
            expectation.ValueKind == JsonValueKind.Object &&
            (expectation.TryGetProperty("color", out _) ||
             expectation.TryGetProperty("changed", out _) ||
             expectation.TryGetProperty("baseline", out _) ||
             expectation.TryGetProperty("baseline_frame_id", out _));

        if (hasExpectation)
        {
            return;
        }

        throw new ToolException(
            "E_INVALID_ARG",
            "assert_region 需要 expectation.color / expectation.changed / baseline",
            new JsonObject { ["field"] = "expectation" },
            retryable: false,
            hint: "示例：{\"changed\":true,\"threshold\":0.01} 或 {\"color\":\"#RRGGBB\",\"coverage\":0.9}",
            stage: "validate");
    }

    private AssertRegionView AssertExpectation(
        byte[] bgra,
        int capturedWidth,
        int capturedHeight,
        int x,
        int y,
        JsonElement expectation)
    {
        if (expectation.TryGetProperty("color", out JsonElement colorElement) &&
            colorElement.ValueKind == JsonValueKind.String)
        {
            return AssertColor(bgra, capturedWidth, capturedHeight, colorElement.GetString()!, expectation);
        }

        if (expectation.TryGetProperty("changed", out JsonElement changedElement) &&
            (changedElement.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return AssertChanged(
                new CapturedImage(bgra, capturedWidth, capturedHeight),
                changedElement.GetBoolean(), expectation, x, y);
        }

        if (expectation.TryGetProperty("baseline", out JsonElement baselineElement) &&
            baselineElement.ValueKind == JsonValueKind.String)
        {
            ValidateBaseline(baselineElement.GetString());
            return AssertChanged(
                new CapturedImage(bgra, capturedWidth, capturedHeight),
                expectChanged: false, expectation, x, y);
        }

        throw new ToolException(
            "E_INVALID_ARG",
            "expectation 需要 color、changed 或 baseline 之一");
    }

    private static void ValidateBaseline(string? raw)
    {
        string baseline = (raw ?? string.Empty).Trim().ToLowerInvariant();

        if (baseline is "last" or "latest" or "last_same_region" or "last-same-region")
        {
            return;
        }

        throw new ToolException(
            "E_INVALID_ARG",
            $"未知 baseline: {baseline}（可选 last_same_region/last）");
    }

    private AssertRegionView AssertColor(
        byte[] bgra, int width, int height, string hex, JsonElement expectation)
    {
        if (!VisualSupport.TryParseHex(hex, out byte r, out byte g, out byte b))
        {
            throw new ToolException("E_INVALID_ARG", $"颜色格式无效: {hex}");
        }

        int tolerance = GetInt(expectation, "tolerance", 10);
        double coverage = GetDouble(expectation, "coverage", 0.9);

        if (tolerance is < 0 or > 255)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"tolerance 必须在 0..255（收到 {tolerance}）",
                new JsonObject { ["field"] = "tolerance", ["value"] = tolerance },
                retryable: false,
                hint: "颜色容差按 0..255 的每通道绝对差计算",
                stage: "validate");
        }

        if (coverage is < 0 or > 1)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"coverage 必须在 0..1（收到 {coverage}）",
                new JsonObject { ["field"] = "coverage", ["value"] = coverage },
                retryable: false,
                hint: "coverage 是比例阈值；1.0 表示全部像素匹配",
                stage: "validate");
        }

        int total = width * height;
        int matched = 0;
        long sumR = 0, sumG = 0, sumB = 0;

        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte pb = bgra[i];
            byte pg = bgra[i + 1];
            byte pr = bgra[i + 2];
            sumB += pb;
            sumG += pg;
            sumR += pr;

            if (Math.Abs(pr - r) <= tolerance &&
                Math.Abs(pg - g) <= tolerance &&
                Math.Abs(pb - b) <= tolerance)
            {
                matched++;
            }
        }

        double ratio = total == 0 ? 0 : (double)matched / total;
        bool passed = ratio >= coverage;

        return new AssertRegionView(
            passed,
            Math.Round(ratio, 4),
            Math.Round(ratio, 4),
            coverage,
            "coverage 是请求阈值；actual_coverage/matched_ratio 是实际覆盖率",
            tolerance,
            hex,
            total == 0
                ? "#000000"
                : $"#{sumR / total:X2}{sumG / total:X2}{sumB / total:X2}",
            new SizeView(width, height),
            ChangedRatio: null, Threshold: null, ExpectChanged: null,
            BaselineFrameId: null, Baseline: null);
    }

    private static void ValidateChangedExpectation(JsonElement expectation)
    {
        double threshold = GetDouble(expectation, "threshold", 0.01);
        int tolerance = GetInt(expectation, "tolerance", 8);

        if (threshold is < 0 or > 1)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"threshold 必须在 0..1（收到 {threshold}）",
                new JsonObject { ["field"] = "threshold", ["value"] = threshold },
                retryable: false,
                hint: "threshold 是变化像素比例；2.0 会恒假、-1 会恒真",
                stage: "validate");
        }

        if (tolerance is < 0 or > 255)
        {
            throw new ToolException(
                "E_INVALID_ARG",
                $"tolerance 必须在 0..255（收到 {tolerance}）",
                new JsonObject { ["field"] = "tolerance", ["value"] = tolerance },
                retryable: false,
                hint: "像素差容差按 0..255 计算",
                stage: "validate");
        }
    }

    private AssertRegionView AssertChanged(
        CapturedImage image, bool expectChanged, JsonElement expectation, int x, int y)
    {
        // 先校验阈值：非法阈值应在“找基线之前”就返回 E_INVALID_ARG，
        // 否则会被 E_NO_BASELINE 掩盖（REG-08）。
        ValidateChangedExpectation(expectation);

        int width = image.Width;
        int height = image.Height;
        FrameSnapshot? baseline = ResolveBaselineFrame(expectation, x, y, width, height);

        if (baseline is null)
        {
            throw new ToolException(
                "E_NO_BASELINE",
                "没有同区域的基线帧；请先 capture_screen 同一 region，或传 baseline_frame_id",
                new JsonObject
                {
                    ["current"] = new JsonObject
                    {
                        ["x"] = x,
                        ["y"] = y,
                        ["width"] = width,
                        ["height"] = height,
                    },
                    ["available_frames"] = FramesJson(),
                });
        }

        double threshold = GetDouble(expectation, "threshold", 0.01);
        int tolerance = GetInt(expectation, "tolerance", 8);
        double ratio = VisualSupport.ChangedRatio(image.Bgra, baseline.Bgra, tolerance);
        bool passed = expectChanged ? ratio >= threshold : ratio <= threshold;

        return new AssertRegionView(
            passed,
            MatchedRatio: null, ActualCoverage: null, Coverage: null, CoverageSemantics: null,
            Tolerance: null, Expected: null, Average: null, Region: null,
            Math.Round(ratio, 4),
            threshold,
            expectChanged,
            baseline.Id,
            new BoundsView(baseline.X, baseline.Y, baseline.Width, baseline.Height));
    }

    internal FrameSnapshot? ResolveBaselineFrame(
        JsonElement source, int x, int y, int width, int height)
    {
        var region = new FrameRegion(x, y, width, height);

        if (TryReadString(source, "baseline_frame_id", out string? frameId))
        {
            // 显式 baseline 也必须校验区域，否则“同尺寸不同区域”会被静默拿来比较（SEC-09）。
            return MatchFrame(_host.FindFrame(frameId ?? string.Empty), region);
        }

        string mode = TryReadString(source, "baseline", out string? requested)
            ? (requested ?? "last_same_region").Trim().ToLowerInvariant()
            : "last_same_region";

        return mode is "last" or "latest"
            ? MatchSize(_host.LatestFrame(), region)
            : _host.FindLatestFrame(x, y, width, height);
    }

    private static bool TryReadString(JsonElement source, string property, out string? value)
    {
        value = null;

        if (source.ValueKind != JsonValueKind.Object ||
            !source.TryGetProperty(property, out JsonElement element) ||
            element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static FrameSnapshot? MatchFrame(FrameSnapshot? frame, FrameRegion region) =>
        frame is not null &&
        frame.X == region.X &&
        frame.Y == region.Y &&
        frame.Width == region.Width &&
        frame.Height == region.Height
            ? frame
            : null;

    private static FrameSnapshot? MatchSize(FrameSnapshot? frame, FrameRegion region) =>
        frame is not null &&
        frame.Width == region.Width &&
        frame.Height == region.Height
            ? frame
            : null;

    private readonly record struct FrameRegion(int X, int Y, int Width, int Height);

    internal JsonArray FramesJson()
    {
        var array = new JsonArray();

        foreach (FrameSnapshot frame in _host.Frames())
        {
            array.Add(new JsonObject
            {
                ["frame_id"] = frame.Id,
                ["x"] = frame.X,
                ["y"] = frame.Y,
                ["width"] = frame.Width,
                ["height"] = frame.Height,
            });
        }

        return array;
    }
}
