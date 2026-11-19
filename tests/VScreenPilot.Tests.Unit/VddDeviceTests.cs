using VScreenPilot.Platform.Displays;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class VddDeviceTests
{
    [Theory]
    [InlineData(0, 0x00, 0x00)]
    [InlineData(1, 0x00, 0x01)]
    [InlineData(7, 0x00, 0x07)]
    [InlineData(15, 0x00, 0x0F)]
    [InlineData(0x1234, 0x12, 0x34)]
    public void RemoveIndexIsEncodedAsBigEndian16(int index, byte high, byte low)
    {
        Span<byte> buffer = stackalloc byte[2];

        VddDevice.EncodeRemoveIndex(index, buffer);

        // 驱动按 16 位大端读取；0x0100(256) 会被判成非法 index。
        Assert.Equal(high, buffer[0]);
        Assert.Equal(low, buffer[1]);
    }

    [Fact]
    public void RemoveIndexDoesNotLeakLittleEndianLowByte()
    {
        Span<byte> buffer = stackalloc byte[2];

        VddDevice.EncodeRemoveIndex(1, buffer);

        Assert.NotEqual(0x01, buffer[0]);
        Assert.Equal(0x01, buffer[1]);
    }
}
