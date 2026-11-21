using System.Text.Json.Nodes;
using VScreenPilot.Session;

namespace VScreenPilot.Diagnostics;

/// <summary>
/// `--cli env` 与 `--doctor` 共用的 artifact 摘要。
/// JSON 组装留在 Server：Automation 由契约测试禁止使用 System.Text.Json.Nodes。
/// </summary>
internal static class ArtifactSummary
{
    public static JsonObject Describe(string profile) => new()
    {
        ["root"] = VScreenPilot.Session.ArtifactStore.RootFor(profile),
        ["retention_days"] = VScreenPilot.Session.ArtifactStore.ResolveRetentionDays(
            Environment.GetEnvironmentVariable(VScreenPilot.Session.ArtifactStore.RetentionEnv)),
    };
}
