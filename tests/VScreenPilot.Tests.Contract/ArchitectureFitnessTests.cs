using System.Text.RegularExpressions;
using Xunit;

namespace VScreenPilot.Tests.Contract;

/// <summary>
/// 架构 Fitness Test：命名空间单归属、IVT 白名单、文件体量与测试数量的棘轮。
/// 方法长度与宿主成员数由 tools/audit/arch_metrics.py --check 覆盖（CI 执行）。
/// 只允许比基线更好；放宽任何一项都必须改这里并在评审中说明。
/// </summary>
public sealed class ArchitectureFitnessTests
{
    private static readonly string Root = FindRepoRoot();
    private static readonly string Src = Path.Combine(Root, "src");

    // R2 起点：5 个 SPLIT 命名空间。R2-2 完成后应缩到 1（Infrastructure），重命名 Mcp 契约后为 0。
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

    private const int MaxFileLines = 1555;
    private const int MaxUntypedCatch = 37;
    private const int MinTestMethods = 171;

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
    public void FilesDoNotGrowBeyondBaseline()
    {
        foreach (string path in SourceFiles(Src))
        {
            int lines = File.ReadAllLines(path).Length;
            Assert.True(
                lines <= MaxFileLines,
                $"{Path.GetRelativePath(Root, path)} = {lines} 行，超过棘轮 {MaxFileLines}。拆分后再提交。");
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
    public void TestMethodCountDoesNotShrink()
    {
        int total = 0;
        foreach (string path in Directory.EnumerateFiles(Path.Combine(Root, "tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            string text = File.ReadAllText(path);
            total += Regex.Count(text, @"\[Fact\]") + Regex.Count(text, @"\[Theory\]");
        }

        Assert.True(total >= MinTestMethods, $"测试方法只有 {total} 个，少于棘轮 {MinTestMethods}。");
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
