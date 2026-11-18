using System.Text;
using System.Text.Json.Nodes;

namespace VScreenPilot.Ipc;

internal sealed record ClientHello(
    string Client,
    string Cwd,
    IReadOnlyList<string> Roots,
    int ProxyPid,
    string Version,
    bool AllowScript,
    bool AllowUnsafeScript,
    string[] AppAllowlist);

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
        catch
        {
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
            if (JsonNode.Parse(line) is not JsonObject json)
            {
                return null;
            }

            // 只接受 VSP/1；其他版本走“未知握手”错误，避免误把它当成新协议。
            if (json["vsp"] is not JsonValue vspValue ||
                !vspValue.TryGetValue(out int vsp) ||
                vsp != 1)
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
            var roots = new List<string>();

            if (json["roots"] is JsonArray rootArray)
            {
                foreach (JsonNode? root in rootArray)
                {
                    string? value = root?.GetValue<string>();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        roots.Add(NormalizePath(value, cwd));
                    }
                }
            }

            if (roots.Count == 0)
            {
                roots.Add(cwd);
            }

            var allowlist = new List<string>();

            if (json["capabilities"]?["app_allowlist"] is JsonArray allowArray)
            {
                foreach (JsonNode? item in allowArray)
                {
                    string? value = item?.GetValue<string>();

                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        allowlist.Add(NormalizePath(value, cwd));
                    }
                }
            }

            int proxyPid = 0;

            if (json["proxy_pid"] is JsonValue proxyPidValue &&
                proxyPidValue.TryGetValue(out int parsedPid) &&
                parsedPid > 0)
            {
                proxyPid = parsedPid;
            }

            string version = json["version"]?.GetValue<string>() ?? string.Empty;

            return new ClientHello(
                json["client"]?.GetValue<string>() ?? "unknown",
                cwd,
                roots,
                proxyPid,
                version,
                json["capabilities"]?["allow_script"]?.GetValue<bool>() ?? false,
                json["capabilities"]?["allow_unsafe_script"]?.GetValue<bool>() ?? false,
                allowlist.ToArray());
        }
        catch
        {
            return null;
        }
    }
}
