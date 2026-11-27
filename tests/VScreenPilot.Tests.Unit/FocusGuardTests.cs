using VScreenPilot.Server.Infrastructure;
using VScreenPilot.Session;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class FocusGuardTests
{
    [Fact]
    public void ResumeKeepsLeasePauseUntilExpiry()
    {
        var guard = CreateGuard();
        guard.Pause(60_000);

        guard.Resume();

        Assert.True(guard.IsPaused);

        guard.Resume(force: true);

        Assert.False(guard.IsPaused);
    }

    [Fact]
    public void ResumeClearsShortInjectionPause()
    {
        var guard = CreateGuard();
        guard.Pause();

        guard.Resume();

        Assert.False(guard.IsPaused);
    }

    [Fact]
    public void ShortLeaseDoesNotShortenExistingLongLease()
    {
        var guard = CreateGuard();
        guard.Pause(60_000);
        long longDeadline = guard.ResumeAtTicks;

        guard.Pause(1_000);

        Assert.True(guard.ResumeAtTicks >= longDeadline);
        Assert.True(guard.IsPaused);
    }

    private static FocusGuard CreateGuard() =>
        new(new FocusContext(
            _ => false,
            _ => false,
            _ => { },
            new LogRing(capacity: 10, fileEnabled: false)));
}
