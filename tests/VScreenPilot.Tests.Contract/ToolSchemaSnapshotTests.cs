using System.Text.Json.Nodes;
using VScreenPilot.Mcp;
using Xunit;

using VScreenPilot.Tools;

namespace VScreenPilot.Tests.Contract;

/// <summary>
/// 工具目录与 schema 必须与 P0 冻结的基线逐字段一致。
/// 基线由 tools/audit/capture_baseline.py 从发布二进制抓取，入库后作为外部契约。
/// </summary>
public sealed class ToolSchemaSnapshotTests
{
    private const string BaselineToolCount = "22";

    private static Dictionary<string, ToolDescriptor> Catalog()
    {
        // trace=false：只构建目录，不读会话、不写 trace。
        return new McpTransport(null!, null!, trace: false).BuildTools().All.ToDictionary(item => item.Name);
    }

    private static string RepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "VScreenPilot.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("找不到仓库根目录（VScreenPilot.slnx）");
        }

        return dir.FullName;
    }

    private static JsonArray BaselineTools()
    {
        string path = Path.Combine(
            RepoRoot(), "tools", "audit", "baseline", "3ddb85b", "server.json");
        JsonNode root = JsonNode.Parse(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"基线无法解析：{path}");
        return root["tools"]?.AsArray()
            ?? throw new InvalidOperationException($"基线缺少 tools：{path}");
    }

    [Fact]
    public void BaselineIsComplete()
    {
        JsonArray tools = BaselineTools();
        Assert.Equal(int.Parse(BaselineToolCount, System.Globalization.CultureInfo.InvariantCulture), tools.Count);
        Assert.All(tools, tool => Assert.False(string.IsNullOrWhiteSpace(tool!["name"]?.GetValue<string>())));
    }

    [Fact]
    public void CatalogMatchesBaselineNames()
    {
        string[] expected = BaselineTools()
            .Select(tool => tool!["name"]!.GetValue<string>())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] actual = Catalog().Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TitlesAndDescriptionsAreFrozen()
    {
        Dictionary<string, ToolDescriptor> catalog = Catalog();

        foreach (JsonNode? tool in BaselineTools())
        {
            string name = tool!["name"]!.GetValue<string>();
            ToolDescriptor definition = catalog[name];

            Assert.Equal(tool["title"]?.GetValue<string>(), definition.Title);
            Assert.Equal(tool["description"]?.GetValue<string>(), definition.Description);
        }
    }

    [Fact]
    public void SchemasAreFrozen()
    {
        Dictionary<string, ToolDescriptor> catalog = Catalog();

        foreach (JsonNode? tool in BaselineTools())
        {
            string name = tool!["name"]!.GetValue<string>();
            ToolDescriptor definition = catalog[name];

            AssertJsonEqual(tool["inputSchema"], definition.InputSchema, $"{name}.inputSchema");
            AssertJsonEqual(tool["outputSchema"], definition.OutputSchema, $"{name}.outputSchema");
        }
    }

    [Fact]
    public void AnnotationsAreFrozen()
    {
        Dictionary<string, ToolDescriptor> catalog = Catalog();

        foreach (JsonNode? tool in BaselineTools())
        {
            string name = tool!["name"]!.GetValue<string>();
            ToolDescriptor definition = catalog[name];
            JsonNode annotations = tool["annotations"]
                ?? throw new InvalidOperationException($"{name} 基线缺少 annotations");

            Assert.Equal(definition.ReadOnly, annotations["readOnlyHint"]!.GetValue<bool>());
            Assert.Equal(definition.Destructive, annotations["destructiveHint"]!.GetValue<bool>());
            Assert.Equal(!definition.ReadOnly, annotations["openWorldHint"]!.GetValue<bool>());
            Assert.Equal(definition.ReadOnly, annotations["idempotentHint"]!.GetValue<bool>());
        }
    }

    private static void AssertJsonEqual(JsonNode? expected, JsonNode? actual, string path)
    {
        if (!JsonNode.DeepEquals(expected, actual))
        {
            Assert.Fail(
                $"{path} 与基线不一致\n期望: {expected?.ToJsonString()}\n实际: {actual?.ToJsonString()}");
        }
    }
}
