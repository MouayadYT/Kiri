using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// Reading a tool call as the model wrote it (PROJECT_SPEC §4.8, §5.5): the arguments are text the model produced and may be anything,
/// so they are read strictly, tidied where a slip cannot change what is meant, and refused otherwise, with words the model can act on.
/// </summary>
public sealed class ToolCallParserTests
{
    private static ParsedToolCall Parse(string arguments, string name = "search_files", string id = "call_1") =>
        ToolCallParser.Parse(new ToolCall(id, name, arguments));

    // ---- The arguments ----

    [Theory]
    [InlineData("""{"query":"milestone"}""", """{"query":"milestone"}""")]
    [InlineData("""  { "query" : "milestone" }  """, """{"query":"milestone"}""")]
    [InlineData("""{ "a": 1, "b": [1, 2], "c": { "d": true } }""", """{"a":1,"b":[1,2],"c":{"d":true}}""")]
    [InlineData("", "{}")]
    [InlineData("   ", "{}")]
    [InlineData("{}", "{}")]
    [InlineData("null", "{}")]
    public void ArgumentsThatAreAJsonObject_AreMadeCompact_AndNoArgumentsIsAnEmptyObject(string written, string expected)
    {
        var parsed = Parse(written);

        Assert.True(parsed.IsValid);
        Assert.Equal(expected, parsed.Call.ArgumentsJson);
        Assert.Equal(JsonValueKind.Object, parsed.Arguments.ValueKind);
        Assert.Equal(expected, parsed.Arguments.GetRawText());
    }

    [Fact]
    public void ASumKeepsItsPlusAndItsAccents_NotEscaped()
    {
        var parsed = Parse("""{ "expression": "9 + 10", "note": "café" }""", "calculate");

        Assert.Equal("""{"expression":"9 + 10","note":"café"}""", parsed.Call.ArgumentsJson);
    }

    [Theory]
    [InlineData("\"{\\\"query\\\":\\\"a\\\"}\"")]
    [InlineData("\"  {\\\"query\\\": \\\"a\\\"}\"")]
    public void AnObjectWrittenAsAStringOfJson_IsTheObject(string written)
    {
        var parsed = Parse(written);

        Assert.True(parsed.IsValid);
        Assert.Equal("""{"query":"a"}""", parsed.Call.ArgumentsJson);
    }

    [Theory]
    [InlineData("```json\n{\"query\":\"a\"}\n```")]
    [InlineData("```\n{\"query\": \"a\"}\n```")]
    [InlineData("```json\n{\"query\":\"a\"}```")]
    public void ArgumentsInAMarkdownCodeFence_AreTheObjectInIt(string written)
    {
        var parsed = Parse(written);

        Assert.True(parsed.IsValid);
        Assert.Equal("""{"query":"a"}""", parsed.Call.ArgumentsJson);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"query\":")]
    [InlineData("{\"query\":\"a\",}")]
    [InlineData("{'query':'a'}")]
    [InlineData("{\"query\":\"a\"} trailing")]
    [InlineData("{\"query\":\"a\"} // comment")]
    [InlineData("{/* c */\"query\":\"a\"}")]
    public void ArgumentsThatAreNotStrictJson_AreRefused_WithWordsThatSayHowToWriteThem(string written)
    {
        var parsed = Parse(written);

        Assert.False(parsed.IsValid);
        Assert.Equal(ToolErrors.InvalidArguments, parsed.Problem!.Code);
        Assert.Contains("one JSON object", parsed.Problem.Message, StringComparison.Ordinal);
        Assert.Equal(written, parsed.Call.ArgumentsJson);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"just words\"")]
    [InlineData("\"[1]\"")]
    public void ArgumentsThatAreNotAnObject_AreRefused(string written)
    {
        var parsed = Parse(written);

        Assert.False(parsed.IsValid);
        Assert.Equal(ToolErrors.InvalidArguments, parsed.Problem!.Code);
        Assert.Contains("JSON object", parsed.Problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnObjectInAStringInAStringIsNotUnwrappedTwice()
    {
        var twice = JsonSerializer.Serialize(JsonSerializer.Serialize("""{"query":"a"}"""));

        Assert.False(Parse(twice).IsValid);
    }

    [Fact]
    public void AnArgumentGivenTwice_IsRefused_BecauseNoOneKnowsWhichWasMeant()
    {
        var parsed = Parse("""{"query":"a","query":"b"}""");

        Assert.False(parsed.IsValid);
        Assert.Equal(ToolErrors.InvalidArguments, parsed.Problem!.Code);
        Assert.Contains("\"query\" is given more than once", parsed.Problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNameOfADuplicatedArgumentIsShownShortAndOnOneLine()
    {
        var name = "x\nsecret" + new string('y', 100);

        var parsed = Parse($$"""{"{{name.Replace("\n", "\\n")}}":1,"{{name.Replace("\n", "\\n")}}":2}""");

        Assert.False(parsed.IsValid);
        Assert.DoesNotContain('\n', parsed.Problem!.Message);
        Assert.True(parsed.Problem.Message.Length < 150);
    }

    [Fact]
    public void ArgumentsNestedTooDeeply_AreRefused()
    {
        var deep = new string('[', 40) + new string(']', 40);

        var parsed = Parse($$"""{"a":{{deep}}}""");

        Assert.False(parsed.IsValid);
        Assert.Equal(ToolErrors.InvalidArguments, parsed.Problem!.Code);
    }

    [Fact]
    public void ArgumentsThatAreTooLong_AreRefused_AndNotKeptInTheCall()
    {
        var huge = "{\"query\":\"" + new string('a', ToolCallParser.MaxArgumentsLength) + "\"}";

        var parsed = Parse(huge);

        Assert.False(parsed.IsValid);
        Assert.Contains("too long", parsed.Problem!.Message, StringComparison.Ordinal);
        Assert.Equal("{}", parsed.Call.ArgumentsJson);
    }

    [Fact]
    public void ArgumentsAtTheLimitAreRead()
    {
        var value = new string('a', ToolCallParser.MaxArgumentsLength - "{\"q\":\"\"}".Length);

        Assert.True(Parse($$"""{"q":"{{value}}"}""").IsValid);
    }

    // ---- The name and the id ----

    [Theory]
    [InlineData("search_files", "search_files")]
    [InlineData("  search_files  ", "search_files")]
    [InlineData("functions.search_files", "search_files")]
    [InlineData("Functions.search_files", "search_files")]
    [InlineData("tools.search_files", "search_files")]
    [InlineData("tool.search_files", "search_files")]
    public void TheNameIsTrimmed_AndMayCarryAFunctionsPrefix(string written, string expected)
    {
        var parsed = Parse("{}", written);

        Assert.True(parsed.IsValid);
        Assert.Equal(expected, parsed.Call.ToolName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ACallWithNoName_IsAnUnknownTool(string name)
    {
        var parsed = Parse("{}", name);

        Assert.False(parsed.IsValid);
        Assert.Equal(ToolErrors.UnknownTool, parsed.Problem!.Code);
    }

    [Fact]
    public void ANameIsNotChangedInAnyOtherWay_SoWhatItNamesIsDecidedByTheRegistryAlone()
    {
        Assert.Equal("Search_Files", Parse("{}", "Search_Files").Call.ToolName);
        Assert.Equal("run_powershell", Parse("{}", "run_powershell").Call.ToolName);
        Assert.Equal("search-files", Parse("{}", "search-files").Call.ToolName);
        Assert.Equal("functions.", Parse("{}", "functions.").Call.ToolName);
    }

    [Fact]
    public void ANameThatIsTooLongIsCut_AndSoIsAnId()
    {
        var parsed = ToolCallParser.Parse(new ToolCall(new string('i', 500), new string('n', 500), "{}"));

        Assert.Equal(ToolCallParser.MaxNameLength, parsed.Call.ToolName.Length);
        Assert.Equal(ToolCallParser.MaxIdLength, parsed.Call.Id.Length);
    }

    [Fact]
    public void ACallWithNothingInIt_IsReadWithoutAnException()
    {
        var parsed = ToolCallParser.Parse(new ToolCall(null!, null!, null!));

        Assert.False(parsed.IsValid);
        Assert.Equal(ToolErrors.UnknownTool, parsed.Problem!.Code);
        Assert.Equal(string.Empty, parsed.Call.ToolName);
    }

    [Fact]
    public void WhatIsWrongNeverRepeatsWhatTheModelWrote()
    {
        var secret = "C:\\Users\\private\\passwords.txt";

        var notJson = Parse(secret);
        var notObject = Parse($"[\"{secret.Replace("\\", "\\\\")}\"]");

        Assert.DoesNotContain("private", notJson.Problem!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private", notObject.Problem!.Message, StringComparison.Ordinal);
    }

    // ---- What the model is told ----

    [Fact]
    public void AnErrorIsToldAsAnObjectWithItsWordsItsCodeAndHowTheToolIsCalled()
    {
        var json = ToolErrors.Json(ToolErrors.InvalidArguments, "The argument \"percent\" must be at most 100.", "set_volume(percent: a whole number from 0 to 100)");

        using var document = JsonDocument.Parse(json);
        Assert.Equal(["error", "code", "usage"], document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.True(ToolErrors.TryRead(json, out var code, out var message));
        Assert.Equal(ToolErrors.InvalidArguments, code);
        Assert.Equal("The argument \"percent\" must be at most 100.", message);
    }

    [Fact]
    public void AnErrorWithoutUsageHasNone_AndAToolsOwnErrorIsReadAsAFailure()
    {
        using var document = JsonDocument.Parse(ToolErrors.Json(ToolErrors.Failed, "It did not work."));
        Assert.False(document.RootElement.TryGetProperty("usage", out _));

        Assert.True(ToolErrors.TryRead("""{"error":"Files are turned off."}""", out var code, out var message));
        Assert.Equal(ToolErrors.Failed, code);
        Assert.Equal("Files are turned off.", message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"found":2}""")]
    [InlineData("""{"error":3}""")]
    public void WhatIsNotAnErrorIsNotRead(string? json)
    {
        Assert.False(ToolErrors.TryRead(json, out _, out _));
    }

    [Fact]
    public void AnErrorResult_IsOfTheCallItAnswers_WhateverTheNameWas()
    {
        var result = ToolErrors.Result(new ToolCall("c9", null!, "{}"), ToolResultStatus.Failed, ToolErrors.UnknownTool, "No.");

        Assert.Equal(("c9", string.Empty, ToolResultStatus.Failed), (result.ToolCallId, result.ToolName, result.Status));
        Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _));
        Assert.Equal(ToolErrors.UnknownTool, code);
    }

    [Theory]
    [InlineData("""{"type":"object","properties":{"percent":{"type":"integer","minimum":0,"maximum":100}},"required":["percent"]}""", "set_volume(percent: a whole number from 0 to 100)")]
    [InlineData("""{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer","minimum":1}}, "required":["query"]}""", "set_volume(query: text, limit?: a whole number of at least 1)")]
    [InlineData("""{"type":"object","properties":{"mode":{"type":"string","enum":["a","b"]},"flag":{"type":"boolean"},"x":{"type":"number","maximum":2.5}}}""", "set_volume(mode?: one of a, b, flag?: true or false, x?: a number of at most 2.5)")]
    [InlineData("""{"type":"object"}""", "set_volume()")]
    [InlineData("""{"type":"object","properties":{}}""", "set_volume()")]
    [InlineData("not json", "set_volume()")]
    public void TheUsageOfAToolIsOneLineFromItsSchema(string schema, string expected)
    {
        var definition = new ToolDefinition("set_volume", "Sets it.", schema, RiskLevel.SideEffect);

        Assert.Equal(expected, ToolUsage.Describe(definition));
    }

    // ---- The typed schema ----

    [Fact]
    public void ATypedSchemaForbidsArgumentsItDoesNotName_AndCarriesRangesAndLengths()
    {
        var schema = ToolSchema.Build(
        [
            new ToolParameter("percent", ToolParameterType.Integer, "The volume.", Minimum: 0, Maximum: 100),
            new ToolParameter("name", ToolParameterType.String, "A name.", Required: false, MaxLength: 80),
        ]);

        using var document = JsonDocument.Parse(schema);
        var root = document.RootElement;
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(0, root.GetProperty("properties").GetProperty("percent").GetProperty("minimum").GetDouble());
        Assert.Equal(100, root.GetProperty("properties").GetProperty("percent").GetProperty("maximum").GetDouble());
        Assert.Equal(80, root.GetProperty("properties").GetProperty("name").GetProperty("maxLength").GetInt32());
        Assert.Equal(["percent"], root.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
    }

    [Fact]
    public void ARangeOrALengthThatMakesNoSense_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => ToolSchema.Build([new ToolParameter("a", ToolParameterType.String, "A.", Minimum: 0)]));
        Assert.Throws<ArgumentException>(() => ToolSchema.Build([new ToolParameter("a", ToolParameterType.Integer, "A.", Minimum: 5, Maximum: 1)]));
        Assert.Throws<ArgumentException>(() => ToolSchema.Build([new ToolParameter("a", ToolParameterType.Number, "A.", Maximum: double.PositiveInfinity)]));
        Assert.Throws<ArgumentException>(() => ToolSchema.Build([new ToolParameter("a", ToolParameterType.Integer, "A.", MaxLength: 5)]));
        Assert.Throws<ArgumentException>(() => ToolSchema.Build([new ToolParameter("a", ToolParameterType.String, "A.", MaxLength: 0)]));
    }
}
