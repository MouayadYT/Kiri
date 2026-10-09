using System.Text.Json;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class ToolDefinitionTests
{
    [Fact]
    public void ThePermissionAndTheTimeAreNotPartOfWhatTheModelHostIsSent()
    {
        var definition = ToolDefinition.Create(
            "read_thing", "Reads a thing.", [new ToolParameter("which", ToolParameterType.String, "Which one.")],
            RiskLevel.ReadOnly, PermissionCapability.Files, TimeSpan.FromSeconds(9));

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(definition));

        Assert.Equal(
            ["Name", "Description", "InputSchemaJson", "RiskLevel"],
            json.RootElement.EnumerateObject().Select(property => property.Name));
    }

    [Fact]
    public void ADefinitionWithoutThemIsAsItWasBefore()
    {
        var definition = new ToolDefinition("read_thing", "Reads.", """{"type":"object"}""", RiskLevel.ReadOnly);

        Assert.Null(definition.RequiredPermission);
        Assert.Null(definition.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(30), definition.EffectiveTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), ToolDefinition.MaxTimeout);
    }

    [Fact]
    public void TheCalculationContractRoundTripsAndRefusesWhatIsNotAResult()
    {
        var json = CalculationToolResults.Result(new CalculationOutput("(1 + 2) * 3", "9"));

        Assert.Equal("""{"expression":"(1 + 2) * 3","result":"9"}""", json);
        Assert.True(CalculationToolResults.TryRead(json, out var output));
        Assert.Equal(new CalculationOutput("(1 + 2) * 3", "9"), output);
        Assert.Equal("""{"expression":"2 + 2"}""", CalculationToolResults.Arguments("2 + 2"));

        foreach (var bad in new[] { null, "", "  ", "[]", "{}", "nope", """{"expression":"1","result":1}""", """{"expression":" ","result":"1"}""" })
        {
            Assert.False(CalculationToolResults.TryRead(bad, out _));
        }
    }
}
