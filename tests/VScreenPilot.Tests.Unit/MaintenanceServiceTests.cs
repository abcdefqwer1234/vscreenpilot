using VScreenPilot.Infrastructure;
using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

/// <summary>产物清理的节流与保留策略。</summary>
public sealed class MaintenanceServiceTests : IDisposable
{
    private const string RetentionEnv = "VSP_ARTIFACT_RETENTION_DAYS";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "vsp-maintenance-tests", Guid.NewGuid().ToString("N"));

    private readonly string? _originalRetention =
        Environment.GetEnvironmentVariable(RetentionEnv);

    public MaintenanceServiceTests()
    {
        Environment.SetEnvironmentVariable(RetentionEnv, "7");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(RetentionEnv, _originalRetention);
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void SweepNowDeletesExpiredSessionDirectory()
    {
        string expired = CreateOldDirectory("20200101-000000-1");
        var service = new MaintenanceService(new FakeHost(_root).ToContext());

        service.SweepNow();

        Assert.False(Directory.Exists(expired));
    }

    [Fact]
    public void SweepNowKeepsCurrentSessionDirectory()
    {
        string current = CreateOldDirectory("20200101-000000-2");
        var service = new MaintenanceService(new FakeHost(_root) { Current = new[] { current } }.ToContext());

        service.SweepNow();

        Assert.True(Directory.Exists(current));
    }

    [Fact]
    public void SweepPeriodicallyIsThrottledForOneHour()
    {
        string first = CreateOldDirectory("20200101-000000-3");
        var service = new MaintenanceService(new FakeHost(_root).ToContext());

        service.SweepNow();
        Assert.False(Directory.Exists(first));

        string second = CreateOldDirectory("20200101-000000-4");
        service.SweepPeriodically();
        Assert.True(Directory.Exists(second));

        service.SweepNow();
        Assert.False(Directory.Exists(second));
    }

    [Fact]
    public void DisposeIsIdempotentAfterStart()
    {
        var service = new MaintenanceService(new FakeHost(_root).ToContext());

        service.Start();
        service.Dispose();
        service.Dispose();
    }

    private string CreateOldDirectory(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "artifact.png"), "x");
        Directory.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-30));
        return path;
    }

    private sealed class FakeHost
    {
        public FakeHost(string root) => ArtifactRoot = root;

        public string ArtifactRoot { get; }

        public bool Disposed { get; set; }

        public string[] Current { get; init; } = Array.Empty<string>();

        public LogRing Log { get; } = new(capacity: 10, fileEnabled: false);

        public MaintenanceContext ToContext() =>
            new(ArtifactRoot, () => Disposed, () => Current, Log, () => { });
    }
}
