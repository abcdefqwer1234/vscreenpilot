using System.Text.RegularExpressions;
using Xunit;

namespace VScreenPilot.Tests.Contract;

/// <summary>
/// 架构边界闸门：命名空间单归属、IVT 白名单、无类型 catch、Automation 不暴露 public 类型。
/// 行数、方法长度、测试数量、组件映射等风格指标不再作为 CI 闸门；
/// 需要查看时使用 <c>tools/audit/arch_metrics.py --inventory</c>。
/// </summary>
public sealed class ArchitectureFitnessTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Src = Path.Combine(Root, "src");

    private static readonly HashSet<string> SplitAllowlist = new(StringComparer.Ordinal);

    private static readonly HashSet<string> IvtAllowlist = new(StringComparer.Ordinal)
    {
        "VScreenPilot.Abstractions->VScreenPilot",
        "VScreenPilot.Abstractions->VScreenPilot.Automation",
        "VScreenPilot.Abstractions->VScreenPilot.Tests.Unit",
        "VScreenPilot.Abstractions->VScreenPilot.Tests.Contract",
        "VScreenPilot.Automation->VScreenPilot",
        "VScreenPilot.Automation->VScreenPilot.Tests.Unit",
        "VScreenPilot.Automation->VScreenPilot.Tests.Contract",
        "VScreenPilot.Platform->VddProbe",
        "VScreenPilot.Platform->VScreenPilot",
        "VScreenPilot.Platform->VScreenPilot.Automation",
        "VScreenPilot.Platform->VScreenPilot.Tests.Unit",
        "VScreenPilot.Platform->VScreenPilot.Tests.Contract",
        "VScreenPilot->VScreenPilot.Tests.Unit",
        "VScreenPilot->VScreenPilot.Tests.Contract",
    };

    private const int MaxUntypedCatch = 0;

    [Fact]
    public void NamespaceBelongsToSingleAssembly()
    {
        var map = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (string path in SourceFiles(Src))
        {
            Match match = Regex.Match(File.ReadAllText(path), @"^\s*namespace\s+([A-Za-z0-9_.]+)\s*[;{]", RegexOptions.Multiline);
            if (!match.Success)
            {
                continue;
            }

            string assembly = Path.GetRelativePath(Src, path).Split(Path.DirectorySeparatorChar)[0];
            if (!map.TryGetValue(match.Groups[1].Value, out HashSet<string>? assemblies))
            {
                map[match.Groups[1].Value] = assemblies = new HashSet<string>(StringComparer.Ordinal);
            }

            assemblies.Add(assembly);
        }

        string[] splits = map.Where(pair => pair.Value.Count > 1).Select(pair => pair.Key).OrderBy(name => name).ToArray();
        Assert.All(splits, name => Assert.Contains(name, SplitAllowlist));
    }

    [Fact]
    public void InternalsVisibleToIsAllowlisted()
    {
        foreach (string project in Directory.EnumerateFiles(Src, "*.csproj", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(project);
            Match assemblyName = Regex.Match(text, "<AssemblyName>([^<]+)</AssemblyName>");
            string from = assemblyName.Success ? assemblyName.Groups[1].Value : Path.GetFileNameWithoutExtension(project);

            foreach (Match match in Regex.Matches(text, "<InternalsVisibleTo\\s+Include=\"([^\"]+)\""))
            {
                string key = $"{from}->{match.Groups[1].Value}";
                Assert.Contains(key, IvtAllowlist);
            }
        }
    }

    [Fact]
    public void UntypedCatchDoesNotGrow()
    {
        int total = 0;
        foreach (string path in SourceFiles(Src))
        {
            string text = Regex.Replace(File.ReadAllText(path), @"//.*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            total += Regex.Count(text, @"catch\s*(?:when\s*\([^)]*\))?\s*\{");
        }

        Assert.True(total <= MaxUntypedCatch, $"无类型 catch {total} 处，超过棘轮 {MaxUntypedCatch}。");
    }

    [Fact]
    public void AutomationExposesNoPublicTypes()
    {
        string automation = Path.Combine(Src, "VScreenPilot.Automation");
        foreach (string path in SourceFiles(automation))
        {
            string text = File.ReadAllText(path);
            Assert.DoesNotMatch(@"(?m)^\s*public\s+(?:sealed\s+|static\s+|abstract\s+|partial\s+)*(?:class|record|struct|enum|interface)\s", text);
        }
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

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
