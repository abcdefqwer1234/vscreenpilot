using VScreenPilot.Errors;
using VScreenPilot.Uia;
using Xunit;

namespace VScreenPilot.Tests.Unit;

public sealed class UiaHostTimeoutTests
{
    [Fact]
    public void StuckGenerationTimesOutAndIsRetired()
    {
        using var gate = new ManualResetEventSlim(false);
        using var host = new UiaHost(
            () =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return null!;
            },
            TimeSpan.FromMilliseconds(100));

        ToolException error = Assert.Throws<ToolException>(() => host.InvokeMsaa(() => 1));

        Assert.Equal("E_TIMEOUT", error.Code);
        Assert.Equal("uia", error.Stage);
        gate.Set();
    }

    [Fact]
    public void TimeoutErrorIsRetryableWithHint()
    {
        using var gate = new ManualResetEventSlim(false);
        using var host = new UiaHost(
            () =>
            {
                gate.Wait(TimeSpan.FromSeconds(10));
                return null!;
            },
            TimeSpan.FromMilliseconds(50));

        ToolException error = Assert.Throws<ToolException>(() => host.InvokeMsaa(() => 1));

        Assert.True(error.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(error.Hint));
        gate.Set();
    }
}
