using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VScreenPilot.Mcp;

/// <summary>
/// 一个 MCP 工具的完整描述：对外元数据、输出 schema、调度策略与处理器。
/// 新增工具只需要注册一个描述符，不再改执行器的分支清单。
/// </summary>
internal sealed record ToolDescriptor(
    string Name,
    string Title,
    string Description,
    JsonNode InputSchema,
    JsonNode OutputSchema,
    bool ReadOnly,
    bool Destructive,
    int TimeoutMs,
    ToolPolicy Policy,
    Func<JsonElement, ToolResponse> Handler);

/// <summary>屏幕租约的获取方式：由工具定义声明，执行器不按工具名硬编码。</summary>
internal enum ScreenLeaseMode
{
    None,
    Always,
    EnsureOption,
    SnapshotOptions,
    WaitCondition,
    IfLiveApps,
}

/// <summary>
/// 单个工具的调度策略。mutates/session 读写/锁超时都在工具定义处声明，
/// 执行器只按策略行事。
/// </summary>
internal sealed record ToolPolicy(
    bool Mutates = false,
    ScreenLeaseMode Lease = ScreenLeaseMode.Always,
    bool SessionRead = true,
    bool SessionWrite = false,
    bool BypassWriteLock = false,
    int WriteTimeoutMs = 30000);

/// <summary>工具描述符的唯一登记处。</summary>
internal sealed class ToolRegistry
{
    private readonly Dictionary<string, ToolDescriptor> _tools = new(StringComparer.Ordinal);

    public int Count => _tools.Count;

    public IEnumerable<ToolDescriptor> All => _tools.Values;

    public void Add(ToolDescriptor descriptor) => _tools[descriptor.Name] = descriptor;

    public bool TryGet(string name, [NotNullWhen(true)] out ToolDescriptor? descriptor) =>
        _tools.TryGetValue(name, out descriptor);
}
