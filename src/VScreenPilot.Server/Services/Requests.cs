namespace VScreenPilot.Services;

/// <summary>快照请求；只承载数据，不藏逻辑。</summary>
internal sealed record SnapshotRequest(
    int? Pid,
    int MaxDepth,
    bool InteractiveOnly,
    int MaxNodes,
    bool IncludeValues,
    bool IncludeScreenshot,
    double Scale,
    bool Annotate = false);

/// <summary>屏幕采集请求；只承载数据。</summary>
internal sealed record CaptureRequest(
    int? X,
    int? Y,
    int? Width,
    int? Height,
    double Scale,
    string? SavePath,
    bool StoreFrame,
    double? PreviewScale,
    bool Clip = true);
