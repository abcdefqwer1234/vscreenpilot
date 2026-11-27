using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

public sealed record ScreenTextLineView(
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("bounds")] BoundsView Bounds);

/// <summary>`read_screen_text` 的输出视图。</summary>
public sealed record ScreenTextView(
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("lines")] IReadOnlyList<ScreenTextLineView> Lines,
    [property: JsonPropertyName("matches")] IReadOnlyList<ScreenTextLineView> Matches,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("returned")] int Returned,
    [property: JsonPropertyName("truncated")] bool Truncated,
    [property: JsonPropertyName("privacy_redactions")] int PrivacyRedactions,
    [property: JsonPropertyName("region")] BoundsView Region,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("upscale")] int? Upscale = null,
    [property: JsonPropertyName("clipped")] bool? Clipped = null,
    [property: JsonPropertyName("ocr_source_width")] int? OcrSourceWidth = null,
    [property: JsonPropertyName("ocr_source_height")] int? OcrSourceHeight = null,
    [property: JsonPropertyName("ocr_scaled_width")] int? OcrScaledWidth = null,
    [property: JsonPropertyName("ocr_scaled_height")] int? OcrScaledHeight = null);

/// <summary>`click_text` 的输出视图。</summary>
public sealed record ClickTextView(
    [property: JsonPropertyName("found")] bool Found,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("bounds")] BoundsView Bounds,
    [property: JsonPropertyName("ambiguous")] bool Ambiguous,
    [property: JsonPropertyName("matches")] IReadOnlyList<ScreenTextLineView> Matches,
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("observed")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ObservedView? Observed,
    [property: JsonPropertyName("privacy_redactions")] int PrivacyRedactions,
    [property: JsonPropertyName("hit_element")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    HitElementView? HitElement = null,
    [property: JsonPropertyName("upscale")] int? Upscale = null,
    [property: JsonPropertyName("clipped")] bool? Clipped = null);
