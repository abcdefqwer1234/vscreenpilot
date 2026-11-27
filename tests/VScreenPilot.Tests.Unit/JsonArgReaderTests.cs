using System.Text.Json;
using VScreenPilot.Errors;
using VScreenPilot.Json;
using Xunit;

namespace VScreenPilot.Tests;

public sealed class JsonArgReaderTests
{
    [Fact]
    public void RequiredIntRejectsStringWithoutLeakingInvalidOperation()
    {
        using JsonDocument document = JsonDocument.Parse("""{"x":"1"}""");

        ToolException exception = Assert.Throws<ToolException>(
            () => JsonArgReader.RequiredInt(document.RootElement, "x"));

        Assert.Equal("E_INVALID_ARG", exception.Code);
    }

    [Fact]
    public void RequiredCoordinateRejectsNullWithoutLeakingInvalidOperation()
    {
        using JsonDocument document = JsonDocument.Parse("""{"x":null}""");

        ToolException exception = Assert.Throws<ToolException>(
            () => JsonArgReader.RequiredCoordinate(document.RootElement, "x"));

        Assert.Equal("E_INVALID_ARG", exception.Code);
    }
}
