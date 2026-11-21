namespace VScreenPilot.Infrastructure;

/// <summary>
/// 与外部行为相关的阈值。此前分散在 McpTransport 与 SessionManager.Snapshot 里，
/// 改一处忘一处就会造成“工具返回的内联/预览规则不一致”。
/// </summary>
internal static class Limits
{
    /// <summary>大图返回的预览缩放比例。</summary>
    public const double PreviewScale = 0.25;

    /// <summary>预览缩放的下限，避免极端 scale 产生 0 尺寸。</summary>
    public const double MinScale = 0.1;

    /// <summary>小于该字节数的 PNG 直接内联，不落 artifact。</summary>
    public const int InlineLimitBytes = 128 * 1024;

    /// <summary>artifact 资源超过该字节数时默认只给预览。</summary>
    public const long ResourcePreviewBytes = 512 * 1024;
}
