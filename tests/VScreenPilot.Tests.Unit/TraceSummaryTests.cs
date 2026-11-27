using System.Text.Json.Nodes;
using VScreenPilot.Mcp;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class TraceSummaryTests
{
    [Fact]
    public void SummaryKeepsStateAndDropsPayloadText()
    {
        JsonObject structured = JsonNode.Parse(
            """
            {
              "code": "ok",
              "ref": "e12",
              "snapshot_id": "s3",
              "verified": false,
              "value": "secret text",
              "written": "secret text",
              "observed": { "changed": true, "elapsed_ms": 12 }
            }
            """)!.AsObject();

        string? summary = McpTelemetry.BuildSummary(structured);
        JsonObject parsed = JsonNode.Parse(summary!)!.AsObject();

        Assert.Equal("e12", parsed["ref"]!.GetValue<string>());
        Assert.Equal("s3", parsed["snapshot_id"]!.GetValue<string>());
        Assert.False(parsed["verified"]!.GetValue<bool>());
        Assert.True(parsed["changed"]!.GetValue<bool>());
        Assert.Null(parsed["value"]);
        Assert.Null(parsed["written"]);
        Assert.Null(parsed["elapsed_ms"]);
    }

    [Fact]
    public void EmptySummaryIsNull()
    {
        Assert.Null(McpTelemetry.BuildSummary(new JsonObject()));
    }
}
