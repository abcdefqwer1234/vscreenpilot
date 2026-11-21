using System.Text.Json.Serialization;

namespace VScreenPilot.Views;

/// <summary>`run_script` 的输出视图。字段与旧 JSON 一致（含 policy/sandbox 提示）。</summary>
public sealed record ScriptView(
    [property: JsonPropertyName("stdout")] string Stdout,
    [property: JsonPropertyName("stderr")] string Stderr,
    [property: JsonPropertyName("exit_code")] int ExitCode,
    [property: JsonPropertyName("timed_out")] bool TimedOut,
    [property: JsonPropertyName("duration_ms")] long DurationMs,
    [property: JsonPropertyName("policy")] string Policy,
    [property: JsonPropertyName("sandbox")] bool Sandbox);
