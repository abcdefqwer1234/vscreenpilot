using System.Text.Json;
using VScreenPilot.Mcp;
using VScreenPilot.Tools;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ActionBatchTests
{
    [Theory]
    [InlineData("click", true)]
    [InlineData("gesture", true)]
    [InlineData("set_element_value", true)]
    [InlineData("press_keys", true)]
    [InlineData("snapshot", true)]
    [InlineData("read_table", true)]
    [InlineData("run_actions", false)]
    [InlineData("run_script", false)]
    [InlineData("lease_input", false)]
    [InlineData("release_environment", false)]
    [InlineData("launch_or_place_app", false)]
    [InlineData("set_display", false)]
    public void WhitelistExcludesSessionLevelTools(string action, bool allowed)
    {
        Assert.Equal(allowed, ActionTools.IsAllowed(action));
    }

    [Fact]
    public void DryRunReturnsPlanWithoutExecuting()
    {
        var tools = new ActionTools(
            new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal),
            null);
        using JsonDocument document = JsonDocument.Parse(
            """{"dry_run":true,"steps":[{"action":"snapshot","args":{}},{"action":"read_table","args":{"max_rows":10}}]}""");

        ToolResponse response = tools.RunActions(document.RootElement);

        Assert.False(response.IsError);
        Assert.True(response.StructuredContent!["dry_run"]!.GetValue<bool>());
        Assert.Equal(2, response.StructuredContent["steps"]!.AsArray().Count);
    }

    [Fact]
    public void DryRunValidatesRequiredArguments()
    {
        var registry = new ToolRegistry();
        registry.Add(new ToolDescriptor(
            "click",
            "click",
            "click",
            System.Text.Json.Nodes.JsonNode.Parse("""{"type":"object","required":["x"]}""")!,
            new System.Text.Json.Nodes.JsonObject(),
            ReadOnly: false,
            Destructive: false,
            15_000,
            new ToolPolicy(),
            _ => ToolResponse.Text(new System.Text.Json.Nodes.JsonObject())));
        var tools = new ActionTools(
            new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal),
            null);
        tools.BindRegistry(registry);
        using JsonDocument document = JsonDocument.Parse(
            """{"dry_run":true,"steps":[{"action":"click","args":{}}]}""");

        var error = Assert.Throws<VScreenPilot.Errors.ToolException>(
            () => tools.RunActions(document.RootElement));

        Assert.Equal("E_INVALID_ARG", error.Code);
    }
}
