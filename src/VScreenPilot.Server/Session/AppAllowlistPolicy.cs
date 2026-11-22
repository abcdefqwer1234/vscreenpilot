using System.Text.Json.Nodes;
using VScreenPilot.Errors;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Session;

/// <summary>应用启动白名单校验所需的最小上下文；委托读取实时值。</summary>
internal sealed record AppAllowlistContext(
    Func<string[]> ClientRoots,
    Func<string, string> ResolvePath);

/// <summary>daemon 全局白名单与客户端白名单取交集。</summary>
internal sealed class AppAllowlistPolicy
{
    private readonly AppAllowlistContext _host;

    public AppAllowlistPolicy(AppAllowlistContext host, string[]? globalRoots = null)
    {
        _host = host;
        GlobalRoots = globalRoots ?? ReadAppAllowlist();
    }

    public string[] GlobalRoots { get; }

    public static string[] ReadAppAllowlist()
    {
        string? raw = Environment.GetEnvironmentVariable("VSP_APP_ALLOWLIST");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Array.Empty<string>();
        }

        var roots = new List<string>();

        foreach (string part in raw.Split(
                     ';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                roots.Add(Path.GetFullPath(part).TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch (Exception exception)
            {
                // 忽略无效路径。
                LogSink.Debug($"忽略异常: {exception.Message}");
            }
        }

        return roots.ToArray();
    }

    public void Ensure(string? path)
    {
        string[] clientAllowlist = _host.ClientRoots() ?? Array.Empty<string>();
        bool hasClientRule = clientAllowlist.Length > 0;
        bool hasGlobalRule = GlobalRoots.Length > 0;

        if ((!hasClientRule && !hasGlobalRule) || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath = ResolveOrThrow(path);

        bool clientOk = IsAllowed(fullPath, clientAllowlist, hasClientRule);
        bool globalOk = IsAllowed(fullPath, GlobalRoots, hasGlobalRule);

        if (clientOk && globalOk)
        {
            return;
        }

        throw NotAllowed(fullPath, clientAllowlist, clientOk);
    }

    private string ResolveOrThrow(string path)
    {
        try
        {
            return _host.ResolvePath(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException("E_INVALID_ARG", $"应用路径无效: {exception.Message}");
        }
    }

    private static bool IsAllowed(string fullPath, string[] roots, bool required) =>
        !required || roots.Any(root => PathPolicySupport.IsUnder(fullPath, root));

    private ToolException NotAllowed(string fullPath, string[] clientAllowlist, bool clientOk)
    {
        // 两层规则取交集：客户端自己的白名单只能收紧 daemon 的全局白名单，不能绕过它。
        string[] failedRoots = clientOk ? GlobalRoots : clientAllowlist;
        var allowedRoots = new JsonArray();

        foreach (string root in failedRoots)
        {
            allowedRoots.Add(root);
        }

        return new ToolException(
            "E_APP_NOT_ALLOWED",
            "该应用不在 VSP_APP_ALLOWLIST 白名单内，拒绝启动",
            new JsonObject
            {
                ["path"] = fullPath,
                ["scope"] = clientOk ? "global" : "client",
                ["allowed_roots"] = allowedRoots,
            });
    }
}
