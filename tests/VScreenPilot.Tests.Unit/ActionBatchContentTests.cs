using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Mcp;
using VScreenPilot.Tools;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class ActionBatchContentTests
{
    [Fact]
    public void BatchForwardsImageBlocksFromNestedSnapshot()
    {
        var handlers = new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal)
        {
            ["snapshot"] = _ => ToolResponse.Text(new JsonObject { ["ok"] = true })
                .AddImage(new byte[] { 1, 2, 3 }),
        };
        var tools = new ActionTools(handlers, null);

        using JsonDocument document = JsonDocument.Parse(
            """{"steps":[{"action":"snapshot","args":{}}]}""");
        ToolResponse response = tools.RunActions(document.RootElement);

        Assert.False(response.IsError);
        Assert.Contains(
            response.Content,
            block => block?["type"]?.GetValue<string>() == "image");
        Assert.Contains(
            response.Content,
            block => block?["type"]?.GetValue<string>() == "text" &&
                     block?["text"]?.GetValue<string>() == "[step 0] snapshot");
    }

    [Fact]
    public void BatchWithFailedStepSetsIsError()
    {
        var handlers = new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal)
        {
            ["snapshot"] = _ => ToolResponse.Error("E_TEST", "failed"),
        };
        var tools = new ActionTools(handlers, null);

        using JsonDocument document = JsonDocument.Parse(
            """{"steps":[{"action":"snapshot","args":{}}]}""");
        ToolResponse response = tools.RunActions(document.RootElement);

        Assert.True(response.IsError);
        Assert.False(response.StructuredContent!["ok"]!.GetValue<bool>());
    }
}
