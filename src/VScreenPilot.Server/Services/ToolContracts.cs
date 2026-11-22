namespace VScreenPilot.Services;

/// <summary>应用启动请求；用 init 属性避免长参数构造。</summary>
internal sealed record AppLaunchRequest
{
    public string? Path { get; init; }
    public string? Window { get; init; }
    public string? Args { get; init; }
    public int WaitMs { get; init; } = 15000;
    public bool NewInstance { get; init; } = true;
    public bool EnsureDocument { get; init; } = true;
    public int? Pid { get; init; }
}

/// <summary>元素查找条件。</summary>
internal sealed record ElementQuery
{
    public string? Name { get; init; }
    public string? ControlType { get; init; }
    public string? AutomationId { get; init; }
    public string NameMatch { get; init; } = "exact";
    public int Index { get; init; }
    public bool All { get; init; }
    public int MaxResults { get; init; } = 20;
    public bool IncludeOffscreen { get; init; }
    public int? Pid { get; init; }
    public string? Role { get; init; }
    public string Prefer { get; init; } = "auto";
}

/// <summary>UI 树查询条件。</summary>
internal sealed record UiTreeQuery
{
    public int? MaxDepth { get; init; }
    public bool? InteractiveOnly { get; init; }
    public int? MaxNodes { get; init; }
    public string? Root { get; init; }
    public int? Pid { get; init; }
    public string? Format { get; init; }
    public bool NewSnapshot { get; init; }
}

/// <summary>像素点击请求。</summary>
internal sealed record ClickRequest
{
    public int X { get; init; }
    public int Y { get; init; }
    public string Button { get; init; } = "left";
    public int Clicks { get; init; } = 1;
    public string Mode { get; init; } = "auto";
    public bool KeepForeground { get; init; }
    public bool Observe { get; init; } = true;
    public bool FallbackInput { get; init; }
}

/// <summary>OCR 点击请求。</summary>
internal sealed record ClickTextRequest
{
    public string Text { get; init; } = string.Empty;
    public bool KeepForeground { get; init; }
    public bool Observe { get; init; } = true;
    public string Channel { get; init; } = "auto";
    public bool RequireUnique { get; init; }
    public int? RegionX { get; init; }
    public int? RegionY { get; init; }
    public int? RegionWidth { get; init; }
    public int? RegionHeight { get; init; }
    public bool FallbackInput { get; init; }
}

/// <summary>屏幕 OCR 读取请求。</summary>
internal sealed record ScreenTextRequest
{
    public int? X { get; init; }
    public int? Y { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Query { get; init; }
    public int MaxLines { get; init; } = 50;
}

/// <summary>环境释放请求。</summary>
internal sealed record ReleaseRequest
{
    public bool KeepDisplay { get; init; } = true;
    public string OnDirty { get; init; } = "abort";
    public bool AllowDataLoss { get; init; }
}
