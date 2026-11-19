using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace VScreenPilot.Ipc;

internal static class ControlClient
{
    public static JsonObject? TryInvoke(
        string profile, JsonObject request, int timeoutMs = 5000, int connectTimeoutMs = 300,
        int retries = 1)
    {
        int attempts = Math.Max(1, retries);

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            NamedPipeClientStream pipe;

            try
            {
                pipe = new NamedPipeClientStream(
                    ".", PipeNames.Ctl(profile), PipeDirection.InOut, PipeOptions.Asynchronous);
            }
            catch
            {
                return null;
            }

            try
            {
                pipe.Connect(Math.Max(50, connectTimeoutMs));
            }
            catch (Exception exception) when (
                attempt < attempts - 1 && (exception is IOException or TimeoutException))
            {
                // daemon 刚启动/刚接受完上一个请求：重试几次，避免把“暂时 busy”当成“没有实例”。
                pipe.Dispose();
                Thread.Sleep(100);
                continue;
            }
            catch
            {
                pipe.Dispose();
                return null;
            }

            using (pipe)
            {
                try
                {
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
                    {
                        AutoFlush = true,
                    };
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

                    writer.WriteLine(request.ToJsonString());
                    writer.Flush();

                    Task<string?> read = reader.ReadLineAsync();

                    if (!read.Wait(Math.Max(200, timeoutMs)))
                    {
                        return null;
                    }

                    string? line = read.Result;
                    return line is null ? null : JsonNode.Parse(line) as JsonObject;
                }
                catch
                {
                    return null;
                }
            }
        }

        return null;
    }
}
