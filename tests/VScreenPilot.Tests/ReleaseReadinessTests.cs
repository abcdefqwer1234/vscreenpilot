using System.Text.Json.Nodes;
using VScreenPilot.Mcp;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ToolCatalogTests
{
    private static readonly string[] Expected =
    [
        "assert_region", "capture_screen", "click", "click_ref", "click_text",
        "find_element", "gesture", "get_element_text", "get_environment", "get_log",
        "get_ui_tree", "invoke_element", "launch_or_place_app", "match_visual",
        "press_keys", "read_screen_text", "release_environment", "run_script",
        "set_element_value", "snapshot", "type_text", "wait_for",
    ];

    private static Dictionary<string, McpServer.ToolDefinition> Catalog()
    {
        // trace=false：只构建目录，不读会话、不写 trace。
        var server = new McpServer(null!, null!, trace: false);
        return server.BuildTools();
    }

    [Fact]
    public void CatalogMatchesPublishedToolSet()
    {
        Dictionary<string, McpServer.ToolDefinition> tools = Catalog();

        Assert.Equal(Expected.Length, tools.Count);
        Assert.Equal(
            Expected.OrderBy(name => name, StringComparer.Ordinal),
            tools.Keys.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryToolHasUsableSchemas()
    {
        foreach ((string name, McpServer.ToolDefinition tool) in Catalog())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Title), name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), name);

            var input = Assert.IsType<JsonObject>(tool.InputSchema);
            Assert.Equal("object", input["type"]!.GetValue<string>());

            var output = Assert.IsType<JsonObject>(tool.OutputSchema);
            Assert.NotNull(output["properties"]);
            Assert.NotEmpty(output["properties"]!.AsObject());
        }
    }

    [Fact]
    public void AnnotationsMatchMutatingSet()
    {
        foreach ((string name, McpServer.ToolDefinition tool) in Catalog())
        {
            if (McpServer.MutatingTools.Contains(name))
            {
                Assert.False(tool.ReadOnly, $"{name} 会改变状态，不能标只读");
            }
            else
            {
                Assert.True(tool.ReadOnly || name is "snapshot" or "capture_screen" or "get_environment",
                    $"{name} 既非写工具也未标只读");
            }

            Assert.InRange(tool.TimeoutMs, 1_000, 130_000);
        }
    }

    [Fact]
    public void RequiredFieldsAreDeclared()
    {
        Dictionary<string, McpServer.ToolDefinition> tools = Catalog();

        AssertRequired(tools, "click", "x", "y");
        AssertRequired(tools, "click_ref", "ref");
        AssertRequired(tools, "invoke_element", "ref");
        AssertRequired(tools, "set_element_value", "ref", "value");
        AssertRequired(tools, "get_element_text", "ref");
        AssertRequired(tools, "type_text", "text");
        AssertRequired(tools, "click_text", "text");
        AssertRequired(tools, "run_script", "code");
        AssertRequired(tools, "wait_for", "condition");
        AssertRequired(tools, "assert_region", "region", "expectation");
        AssertRequired(tools, "match_visual", "mode");
    }

    private static void AssertRequired(
        Dictionary<string, McpServer.ToolDefinition> tools, string name, params string[] expected)
    {
        var schema = Assert.IsType<JsonObject>(tools[name].InputSchema);
        var required = Assert.IsType<JsonArray>(schema["required"]);

        foreach (string field in expected)
        {
            Assert.Contains(field, required.Select(item => item!.GetValue<string>()));
        }
    }
}

public sealed class ArtifactRetentionTests
{
    private static readonly DateTime Now = new(2026, 11, 18, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, 7)]
    [InlineData("", 7)]
    [InlineData("0", 0)]
    [InlineData("3", 3)]
    [InlineData(" 5 ", 5)]
    [InlineData("-1", 7)]
    [InlineData("abc", 7)]
    public void RetentionDaysFallBackSafely(string? raw, int expected)
    {
        Assert.Equal(expected, VScreenPilot.Session.SessionManager.ResolveArtifactRetentionDays(raw));
    }

    [Fact]
    public void DeletesExpiredSessionDirectory()
    {
        Assert.True(ShouldDelete("20260101-120000-123", Now.AddDays(-8), 7, false));
    }

    [Fact]
    public void KeepsRecentDirectory()
    {
        Assert.False(ShouldDelete("20260101-120000-123", Now.AddDays(-6), 7, false));
    }

    [Fact]
    public void ZeroDisablesCleanup()
    {
        Assert.False(ShouldDelete("20260101-120000-123", Now.AddYears(-1), 0, false));
    }

    [Fact]
    public void CurrentSessionIsNeverDeleted()
    {
        Assert.False(ShouldDelete("20260101-120000-123", Now.AddDays(-8), 7, true));
    }

    [Fact]
    public void ForeignDirectoryNamesAreIgnored()
    {
        Assert.False(ShouldDelete("important-backup", Now.AddDays(-30), 7, false));
        Assert.False(ShouldDelete("20260101-120000", Now.AddDays(-30), 7, false));
    }

    [Fact]
    public void ExactCutoffCountsAsExpired()
    {
        Assert.True(ShouldDelete("20260101-120000-123", Now.AddDays(-7), 7, false));
    }

    private static bool ShouldDelete(string name, DateTime lastWrite, int days, bool current) =>
        VScreenPilot.Session.SessionManager.ShouldDeleteArtifactDirectory(
            name, lastWrite, Now, days, current);
}

public sealed class ErrorDocsTests
{
    [Fact]
    public void ErrorDocsPointToPublicUrlWithAnchor()
    {
        ToolResponse response = ToolResponse.Error("E_INVALID_ARG", "bad");
        var payload = JsonNode.Parse(response.Content[0]!["text"]!.GetValue<string>())!;

        string docs = payload["docs"]!.GetValue<string>();
        Assert.StartsWith("https://", docs);
        Assert.EndsWith("#e_invalid_arg", docs);
    }

    [Fact]
    public void RequestIdIsMirroredInTextAndStructuredContent()
    {
        ToolResponse response = ToolResponse.Text(new JsonObject { ["ok"] = true })
            .SetRequestId("req-123");

        Assert.Equal("req-123", response.StructuredContent!["request_id"]!.GetValue<string>());
        Assert.Contains(
            "req-123",
            response.Content[0]!["text"]!.GetValue<string>());
    }
}
