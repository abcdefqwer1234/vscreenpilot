using System.Text.Json.Nodes;
using VScreenPilot.Mcp;
using VScreenPilot.Errors;

namespace VScreenPilot.Session;

/// <summary>
/// 客户端路径策略：相对路径按调用方 cwd 解析；截图落盘只允许 cwd/roots/系统临时目录，
/// 并解析 junction/symlink，避免用目录链接绕过根目录限制。
/// </summary>
internal sealed class ClientPathPolicy
{
    private readonly ClientSessionRegistry _clients;

    public ClientPathPolicy(ClientSessionRegistry clients) => _clients = clients;

    public string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(path, Path.GetFullPath(_clients.CurrentCwd));
    }

    public string ValidateSavePath(string savePath) => ValidatePngPath(savePath, "save_path");

    public string ValidatePngPath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ToolException("E_INVALID_ARG", $"{label} 不能为空");
        }

        string fullPath;

        try
        {
            // 相对 save_path/target_path 必须按调用方 cwd 解析，不能用 daemon cwd。
            fullPath = Resolve(path);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ToolException("E_INVALID_ARG", $"{label} 无效: {exception.Message}");
        }

        // 解析符号链接/junction，避免用“工作目录内的链接”绕过根目录限制。
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(fullPath, returnFinalTarget: true);

            if (target is not null)
            {
                fullPath = target.FullName;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException){
            // 文件尚不存在/无权限读取链接：保留原路径，后续 CreateNew 会给出明确 IO 错误。
            System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
        }

        // 文件本身不存在时，父目录仍可能是 junction；逐级解析，防止写到允许根之外。
        fullPath = ResolveParentLinks(fullPath);

        if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ToolException("E_INVALID_ARG", $"{label} 只允许 .png 文件");
        }

        string tempRoot = Path.GetTempPath();
        var allowedRoots = new List<string> { _clients.CurrentCwd };
        foreach (string root in _clients.CurrentRoots)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                allowedRoots.Add(root);
            }
        }

        allowedRoots.Add(Path.GetFullPath(tempRoot));

        if (!allowedRoots.Any(root => PathPolicySupport.IsUnder(fullPath, root)))
        {
            var rootsJson = new JsonArray();
            foreach (string root in allowedRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                rootsJson.Add(root);
            }

            throw new ToolException(
                "E_INVALID_ARG",
                $"{label} 只能落在调用方工作目录或系统临时目录内",
                new JsonObject
                {
                    ["path"] = fullPath,
                    ["allowed_roots"] = rootsJson,
                });
        }

        return fullPath;
    }

    private static string ResolveParentLinks(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);

        while (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            try
            {
                if (Directory.ResolveLinkTarget(directory, returnFinalTarget: true) is DirectoryInfo target)
                {
                    return Path.Combine(
                        target.FullName, Path.GetRelativePath(directory, fullPath));
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException){
                // 无权限读取链接时继续向上；最终仍会受 IsUnder 限制。
                System.Diagnostics.Debug.WriteLine($"忽略异常: {exception.Message}");
            }

            directory = Path.GetDirectoryName(directory);
        }

        return fullPath;
    }
}
