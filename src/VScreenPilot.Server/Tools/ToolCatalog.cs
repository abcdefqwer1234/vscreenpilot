using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VScreenPilot.Mcp;

/// <summary>22 个工具的定义从 Tools/Definitions/*.json 加载，处理器在下方集中登记。</summary>
internal sealed partial class McpTransport
{
    private const string DefinitionResourceMarker = ".Tools.Definitions.";

    internal ToolRegistry BuildTools()
    {
        var handlers = new Dictionary<string, Func<JsonElement, ToolResponse>>(StringComparer.Ordinal)
        {
            ["get_environment"] = GetEnvironment,
            ["launch_or_place_app"] = LaunchOrPlace,
            ["snapshot"] = Snapshot,
            ["capture_screen"] = CaptureScreen,
            ["find_element"] = FindElement,
            ["get_ui_tree"] = GetUiTree,
            ["invoke_element"] = InvokeElement,
            ["set_element_value"] = SetElementValue,
            ["get_element_text"] = GetElementText,
            ["read_screen_text"] = ReadScreenText,
            ["click"] = Click,
            ["click_ref"] = ClickRef,
            ["gesture"] = Gesture,
            ["click_text"] = ClickText,
            ["type_text"] = TypeText,
            ["press_keys"] = PressKeys,
            ["wait_for"] = WaitFor,
            ["assert_region"] = AssertRegion,
            ["match_visual"] = MatchVisual,
            ["release_environment"] = ReleaseEnvironment,
            ["get_log"] = GetLog,
            ["run_script"] = RunScript,
        };

        var tools = new ToolRegistry();
        Assembly assembly = typeof(McpTransport).Assembly;

        foreach (string resource in assembly.GetManifestResourceNames()
                     .Where(name => name.Contains(DefinitionResourceMarker, StringComparison.Ordinal) &&
                                    name.EndsWith(".json", StringComparison.Ordinal))
                     .OrderBy(name => name, StringComparer.Ordinal))
        {
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"缺少工具定义资源: {resource}");

            JsonNode node = JsonNode.Parse(stream)
                ?? throw new InvalidOperationException($"工具定义无法解析: {resource}");

            string name = node["name"]?.GetValue<string>()
                ?? throw new InvalidOperationException($"工具定义缺少 name: {resource}");

            if (!handlers.TryGetValue(name, out Func<JsonElement, ToolResponse>? handler))
            {
                throw new InvalidOperationException($"工具 {name} 没有登记 handler");
            }

            tools.Add(new ToolDescriptor(
                name,
                node["title"]?.GetValue<string>() ?? name,
                node["description"]?.GetValue<string>() ?? string.Empty,
                node["inputSchema"]?.DeepClone()
                    ?? throw new InvalidOperationException($"工具 {name} 缺少 inputSchema"),
                node["outputSchema"]?.DeepClone()
                    ?? throw new InvalidOperationException($"工具 {name} 缺少 outputSchema"),
                node["readOnly"]?.GetValue<bool>() ?? false,
                node["destructive"]?.GetValue<bool>() ?? false,
                node["timeoutMs"]?.GetValue<int>() ?? 30_000,
                ParsePolicy(node["policy"]),
                handler));
        }

        if (tools.Count != handlers.Count)
        {
            string[] missing = handlers.Keys
                .Except(tools.All.Select(tool => tool.Name), StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            throw new InvalidOperationException(
                $"工具定义缺失 {missing.Length} 个: {string.Join(", ", missing)}");
        }

        return tools;
    }

    private static ToolPolicy ParsePolicy(JsonNode? node)
    {
        if (node is null)
        {
            return new ToolPolicy();
        }

        string leaseName = node["lease"]?.GetValue<string>() ?? nameof(ScreenLeaseMode.Always);

        if (!Enum.TryParse(leaseName, ignoreCase: true, out ScreenLeaseMode lease))
        {
            throw new InvalidOperationException($"未知的屏幕租约模式: {leaseName}");
        }

        return new ToolPolicy(
            node["mutates"]?.GetValue<bool>() ?? false,
            lease,
            node["sessionRead"]?.GetValue<bool>() ?? true,
            node["sessionWrite"]?.GetValue<bool>() ?? false,
            node["bypassWriteLock"]?.GetValue<bool>() ?? false,
            node["writeTimeoutMs"]?.GetValue<int>() ?? 30_000);
    }
}
