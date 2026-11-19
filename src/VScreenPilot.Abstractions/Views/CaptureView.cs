using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>
/// `capture_screen` 的领域视图（不含 Server 侧的 artifact/内联决策字段，
/// 那些由工具层在编码后补：inline / artifact_path / artifact_uri / resource_uri / artifact_error）。
/// </summary>
public sealed record CaptureView(
    [property: JsonPropertyName("frame_id")] string FrameId,
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("source_width")] int SourceWidth,
    [property: JsonPropertyName("source_height")] int SourceHeight,
    [property: JsonPropertyName("scale")] double Scale,
    [property: JsonPropertyName("png_bytes")] int PngBytes,
    [property: JsonPropertyName("duration_ms")] double DurationMs,
    [property: JsonPropertyName("encode_ms")] double EncodeMs,
    [property: JsonPropertyName("backend")] string Backend,
    [property: JsonPropertyName("slow_capture")] bool SlowCapture,
    [property: JsonPropertyName("baseline")] BaselineView Baseline,
    [property: JsonPropertyName("path")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Path);

/// <summary>assert_region 的基线：未缩放的源帧矩形。</summary>
public sealed record BaselineView(
    [property: JsonPropertyName("x")] int X,
    [property: JsonPropertyName("y")] int Y,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height);

/// <summary>与主图同帧的缩略图元数据。</summary>
public sealed record PreviewView(
    [property: JsonPropertyName("scale")] double Scale,
    [property: JsonPropertyName("width")] int Width,
    [property: JsonPropertyName("height")] int Height,
    [property: JsonPropertyName("png_bytes")] int PngBytes,
    [property: JsonPropertyName("source_frame_id")] string SourceFrameId);
