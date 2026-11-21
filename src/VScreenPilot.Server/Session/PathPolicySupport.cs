namespace VScreenPilot.Session;

/// <summary>路径边界判断的单一实现，供客户端路径策略与应用白名单共用。</summary>
internal static class PathPolicySupport
{
    /// <summary>路径是否位于根目录下（大小写不敏感；root 会先规范化为绝对路径）。</summary>
    public static bool IsUnder(string path, string root)
    {
        string normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return path.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
