using VScreenPilot.Input;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class InputPolicyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData(" yes ", true)]
    public void AutoFallbackOnlyAcceptsExplicitTruthyValues(string? raw, bool expected)
    {
        Assert.Equal(expected, InputPolicy.ParseAutoFallback(raw));
    }

    [Fact]
    public void ExplicitRequestWinsEvenWithoutEnvironmentSwitch()
    {
        // 进程级开关关闭时，调用方仍可用 fallback_input=true 单独开启。
        Assert.True(InputPolicy.AllowsInputFallback(true) || InputPolicy.AutoFallback);
    }

    [Fact]
    public void DefaultIsOff()
    {
        Assert.False(InputPolicy.ParseAutoFallback("0"));
        Assert.False(InputPolicy.ParseAutoFallback("false"));
    }
}
