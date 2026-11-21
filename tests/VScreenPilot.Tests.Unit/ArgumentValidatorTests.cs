using System.Text.Json;
using System.Text.Json.Nodes;
using VScreenPilot.Mcp;
using Xunit;
using VScreenPilot.Errors;

namespace VScreenPilot.Tests;

public sealed class ArgumentValidatorTests
{
    private static JsonNode Schema(string json) => JsonNode.Parse(json)!;

    private static JsonElement Args(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    private static ToolException Invalid(JsonNode schema, string json) =>
        Assert.Throws<ToolException>(() => ArgumentValidator.Validate(schema, Args(json)));

    [Fact]
    public void AcceptsValidNestedArguments()
    {
        JsonNode schema = Schema(
            """
            {"type":"object","additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{
                "x":{"type":"integer"},"width":{"type":"integer","minimum":1}},
                "required":["x","width"]}}}
            """);

        ArgumentValidator.Validate(schema, Args("""{"region":{"x":1,"width":10}}"""));
    }

    [Fact]
    public void RejectsWrongNestedType()
    {
        JsonNode schema = Schema(
            """
            {"type":"object","additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{
                "width":{"type":"integer","minimum":1}},"required":["width"]}}}
            """);

        ToolException error = Invalid(schema, """{"region":{"width":"wide"}}""");
        Assert.Equal("E_INVALID_ARG", error.Code);
        Assert.Equal("region.width", ((JsonObject)error.Details!)["path"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsUnknownNestedField()
    {
        JsonNode schema = Schema(
            """
            {"type":"object","additionalProperties":false,"properties":{
              "region":{"type":"object","additionalProperties":false,"properties":{"width":{"type":"integer"}}}}}
            """);

        ToolException error = Invalid(schema, """{"region":{"width":10,"widht":1}}""");
        Assert.Equal("E_INVALID_ARG", error.Code);
    }

    [Fact]
    public void RejectsOutOfRange()
    {
        JsonNode schema = Schema(
            """{"type":"object","properties":{"coverage":{"type":"number","minimum":0,"maximum":1}}}""");

        ToolException error = Invalid(schema, """{"coverage":1.5}""");
        Assert.Equal("E_INVALID_ARG", error.Code);
        Assert.Equal("coverage", ((JsonObject)error.Details!)["path"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsNonMatchingOneOf()
    {
        JsonNode schema = Schema(
            """
            {"type":"object","properties":{"expectation":{"oneOf":[
              {"type":"object","required":["color"],"properties":{"color":{"type":"string"}}},
              {"type":"object","required":["changed"],"properties":{"changed":{"type":"boolean"}}}]}}}
            """);

        ToolException error = Invalid(schema, """{"expectation":{"color":1}}""");
        Assert.Equal("E_INVALID_ARG", error.Code);
    }

    [Fact]
    public void RejectsAmbiguousOneOf()
    {
        JsonNode schema = Schema(
            """
            {"type":"object","properties":{"value":{"oneOf":[
              {"type":"object"},
              {"type":"object"}]}}}
            """);

        ToolException error = Invalid(schema, """{"value":{}}""");
        Assert.Equal("E_INVALID_ARG", error.Code);
        Assert.Contains("matches", error.Details!.ToJsonString());
    }

    [Fact]
    public void RejectsInvalidArrayItemPath()
    {
        JsonNode schema = Schema(
            """{"type":"object","properties":{"scales":{"type":"array","items":{"type":"number","maximum":2}}}}""");

        ToolException error = Invalid(schema, """{"scales":[1,2,3]}""");
        Assert.Equal("E_INVALID_ARG", error.Code);
        Assert.Equal("scales[2]", ((JsonObject)error.Details!)["path"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsShortString()
    {
        JsonNode schema = Schema(
            """{"type":"object","properties":{"text":{"type":"string","minLength":2}}}""");

        Assert.Equal("E_INVALID_ARG", Invalid(schema, """{"text":"a"}""").Code);
    }

    [Fact]
    public void MissingRequiredFieldIsReported()
    {
        JsonNode schema = Schema(
            """{"type":"object","required":["code"],"properties":{"code":{"type":"string"}}}""");

        ToolException error = Invalid(schema, "{}");
        Assert.Equal("E_INVALID_ARG", error.Code);
        Assert.Equal("code", ((JsonObject)error.Details!)["path"]!.GetValue<string>());
    }
}
