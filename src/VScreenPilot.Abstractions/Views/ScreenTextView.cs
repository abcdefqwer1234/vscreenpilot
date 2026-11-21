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
    [property: JsonPropertyName("duration_ms")] double DurationMs);
