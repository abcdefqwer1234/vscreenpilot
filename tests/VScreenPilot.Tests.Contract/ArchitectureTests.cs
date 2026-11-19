using System.Reflection;
using Xunit;

namespace VScreenPilot.Tests.Contract;

/// <summary>
/// 编译期边界：4 个程序集的引用图只允许单向。
/// 用反射读 <see cref="Assembly.GetReferencedAssemblies"/>，不引入任何第三方断言库。
/// </summary>
public sealed class ArchitectureTests
{
    private static Assembly Load(string name) => Assembly.Load(new AssemblyName(name));

    private static string[] References(string assembly) =>
        Load(assembly)
            .GetReferencedAssemblies()
            .Select(item => item.Name ?? string.Empty)
            .ToArray();

    [Fact]
    public void AbstractionsDependsOnNothingInternal()
    {
        string[] references = References("VScreenPilot.Abstractions");
        Assert.DoesNotContain(
            references, name => name.StartsWith("VScreenPilot.", StringComparison.Ordinal));
    }

    [Fact]
    public void PlatformDependsOnlyOnAbstractions()
    {
        string[] references = References("VScreenPilot.Platform");
        Assert.Contains("VScreenPilot.Abstractions", references);
        Assert.DoesNotContain("VScreenPilot.Automation", references);
        Assert.DoesNotContain("VScreenPilot", references);
    }

    [Fact]
    public void AutomationDoesNotDependOnServerOrMcpSdk()
    {
        string[] references = References("VScreenPilot.Automation");
        Assert.Contains("VScreenPilot.Abstractions", references);
        Assert.Contains("VScreenPilot.Platform", references);
        Assert.DoesNotContain("VScreenPilot", references);
        Assert.DoesNotContain("VScreenPilot.Server", references);
        Assert.DoesNotContain(
            references, name => name.Contains("ModelContextProtocol", StringComparison.Ordinal));
    }

    [Fact]
    public void ServerDependsOnAutomation()
    {
        string[] references = References("VScreenPilot");
        Assert.Contains("VScreenPilot.Automation", references);
        Assert.Contains("VScreenPilot.Platform", references);
        Assert.Contains("VScreenPilot.Abstractions", references);
    }

    /// <summary>
    /// P3–P4 期间 Automation 仍借用 UiaElementInfo.ToJson；P5 删除 McpServer 门面后必须为 0。
    /// 该断言先跳过，但已入库，避免遗漏。
    /// </summary>
    [Fact(Skip = "P5：UiaElementInfo 序列化移到 ElementPresenter 后启用")]
    public void AutomationHasNoSystemTextJsonNodes()
    {
        string repoRoot = FindRepoRoot();
        string directory = Path.Combine(repoRoot, "src", "VScreenPilot.Automation");
        string[] offenders = Directory
            .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(
                "System.Text.Json.Nodes", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray()!;

        Assert.Empty(offenders);
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "VScreenPilot.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("找不到仓库根目录（VScreenPilot.slnx）");
    }
}
