using Assistant.Core.Agent;
using Assistant.Core.Contracts;
using Assistant.UI.Bootstrap;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// What the app's own tools cost in a prompt. The tools that are always there are offered with every request, so they must leave room in the selector's
/// budget for the ones a request is about (the messaging tools for "yes", the clock's for a timer, a connected app's): in 0.1.142 they did not.
/// </summary>
public sealed class ToolBudgetTests
{
    [Fact]
    public void TheToolsThatAreAlwaysThereLeaveRoomForTheOnesARequestIsAbout()
    {
        using var host = AppHost.Create();
        var registry = host.Services.GetRequiredService<IToolRegistry>();

        // A reply that is about nothing in particular: what is offered for it is what is always there. Nothing is prepared, so nothing is loaded or connected to.
        var always = registry.ToolsFor(new ToolContext(Guid.NewGuid(), "yes"));
        var cost = always.Sum(AgentToolBudget.Cost);

        Assert.True(
            cost <= AgentToolBudget.Default.MaxSchemaCharacters - 3000,
            $"The tools that are always there take {cost} characters: " + string.Join(", ", always.Select(tool => $"{tool.Name} {AgentToolBudget.Cost(tool)}")));
        Assert.DoesNotContain(always, tool => tool.RunsWithoutAsking);
    }
}
