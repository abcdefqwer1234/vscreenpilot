using System.Text;
using System.Text.Json.Nodes;
using VScreenPilot.Platform.Logging;

namespace VScreenPilot.Ipc;

internal sealed record ClientHello(
    string Client,
    string Cwd,
    IReadOnlyList<string> Roots,
    int ProxyPid,
    string Version,
    bool AllowScript,
    bool AllowUnsafeScript,
    string[] AppAllowlist,
    bool ReadOnly);

internal static class Handshake
{
    public const int MaxBytes = 64 * 1024;

    public static async Task<string?> ReadLineAsync(
        Stream stream, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[4096];

        while (buffer.Length < MaxBytes)
        {
            int room = (int)Math.Min(chunk.Length, MaxBytes - buffer.Length);
            int read = await stream.ReadAsync(
                chunk.AsMemory(0, room), cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                return null;
            }

            int newline = Array.IndexOf(chunk, (byte)'\n', 0, read);

            if (newline >= 0)
            {
                buffer.Write(chunk, 0, newline);
                return Encoding.UTF8.GetString(buffer.ToArray()).TrimEnd('\r');
            }

            buffer.Write(chunk, 0, read);
        }

        return null;
    }

    public static async Task WriteLineAsync(
        Stream stream, JsonObject payload, CancellationToken cancellationToken = default)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString() + "\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string NormalizePath(string value, string basePath)
    {
        try
        {
            return Path.GetFullPath(value, basePath);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return basePath;
        }
    }

    public static ClientHello? ParseClientHello(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            if (JsonNode.Parse(line) is not JsonObject json || !HasSupportedProtocol(json))
            {
                return null;
            }

            string rawCwd = json["cwd"]?.GetValue<string>() ?? string.Empty;

            // 不把缺失/相对的 cwd 解释成 daemon 的 cwd：路径基准必须由调用方明确提供。
            if (string.IsNullOrWhiteSpace(rawCwd) || !Path.IsPathRooted(rawCwd))
            {
                return null;
            }

            string cwd = Path.GetFullPath(rawCwd);
            return BuildHello(json, cwd);
        }
        catch (Exception exception)
        {
            LogSink.Debug($"忽略异常: {exception.Message}");
            return null;
        }
    }

    /// <summary>只接受 VSP/1；其他版本走“未知握手”错误，避免误把它当成新协议。</summary>
    private static bool HasSupportedProtocol(JsonObject json) =>
        json["vsp"] is JsonValue vspValue &&
        vspValue.TryGetValue(out int vsp) &&
        vsp == 1;

    private static ClientHello BuildHello(JsonObject json, string cwd) =>
        new(
            json["client"]?.GetValue<string>() ?? "unknown",
            cwd,
            ReadRoots(json, cwd),
            ReadProxyPid(json),
            json["version"]?.GetValue<string>() ?? string.Empty,
            json["capabilities"]?["allow_script"]?.GetValue<bool>() ?? false,
            json["capabilities"]?["allow_unsafe_script"]?.GetValue<bool>() ?? false,
            ReadAllowlist(json, cwd).ToArray(),
            json["capabilities"]?["read_only"]?.GetValue<bool>() ?? false);

    private static List<string> ReadRoots(JsonObject json, string cwd)
    {
        List<string> roots = ReadPathArray(json["roots"] as JsonArray, cwd);

        if (roots.Count == 0)
        {
            roots.Add(cwd);
        }

        return roots;
    }

    private static List<string> ReadAllowlist(JsonObject json, string cwd) =>
        ReadPathArray(json["capabilities"]?["app_allowlist"] as JsonArray, cwd);

    private static List<string> ReadPathArray(JsonArray? array, string cwd)
    {
        var paths = new List<string>();

        if (array is null)
        {
            return paths;
        }

        foreach (JsonNode? item in array)
        {
            string? value = item?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(value))
            {
                paths.Add(NormalizePath(value, cwd));
            }
        }

        return paths;
    }

    private static int ReadProxyPid(JsonObject json)
    {
        if (json["proxy_pid"] is JsonValue value &&
            value.TryGetValue(out int parsedPid) &&
            parsedPid > 0)
        {
            return parsedPid;
        }

        return 0;
    }
}
