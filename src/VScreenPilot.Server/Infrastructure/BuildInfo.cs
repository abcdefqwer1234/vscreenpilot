namespace VScreenPilot.Infrastructure;

/// <summary>对外版本号；安装包、MCP serverInfo 与 --version 保持一致。</summary>
internal static class BuildInfo
{
    public const string Version = "0.2.0";

    /// <summary>错误目录的公开地址；可用 VSP_DOCS_BASE_URL 指向镜像或本地文档。</summary>
    public const string DefaultDocsErrorsUrl =
        "https://github.com/abcdefqwer1234/vscreenpilot/blob/main/docs/errors.md";

    public static string DocsErrorsUrl
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable("VSP_DOCS_BASE_URL");
            return string.IsNullOrWhiteSpace(value)
                ? DefaultDocsErrorsUrl
                : value.Trim().TrimEnd('/');
        }
    }
}
