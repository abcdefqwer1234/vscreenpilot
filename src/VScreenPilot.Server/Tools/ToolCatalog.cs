using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Server.Infrastructure;

namespace VScreenPilot.Tools;

/// <summary>从 Tools/Definitions/*.json 加载 25 个工具定义，并校验 handler 覆盖。</summary>
internal static class ToolCatalog
{
    private const string DefinitionResourceMarker = ".Tools.Definitions.";

    public static ToolRegistry Build(
        IReadOnlyDictionary<string, Func<JsonElement, ToolResponse>> handlers)
    {
        var tools = new ToolRegistry();
        Assembly assembly = typeof(ToolCatalog).Assembly;

        foreach (string resource in EnumerateDefinitions(assembly))
        {
            tools.Add(LoadDescriptor(assembly, resource, handlers));
        }

        EnsureAllHandlersCovered(tools, handlers);
        return tools;
    }

    private static IEnumerable<string> EnumerateDefinitions(Assembly assembly) =>
        assembly.GetManifestResourceNames()
            .Where(name => name.Contains(DefinitionResourceMarker, StringComparison.Ordinal) &&
                           name.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal);

    private static ToolDescriptor LoadDescriptor(
        Assembly assembly,
        string resource,
        IReadOnlyDictionary<string, Func<JsonElement, ToolResponse>> handlers)
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

        return new ToolDescriptor(
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
            handler);
    }

    private static void EnsureAllHandlersCovered(
        ToolRegistry tools,
        IReadOnlyDictionary<string, Func<JsonElement, ToolResponse>> handlers)
    {
        if (tools.Count == handlers.Count)
        {
            return;
        }

        string[] missing = handlers.Keys
            .Except(tools.All.Select(tool => tool.Name), StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        throw new InvalidOperationException(
            $"工具定义缺失 {missing.Length} 个: {string.Join(", ", missing)}");
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
