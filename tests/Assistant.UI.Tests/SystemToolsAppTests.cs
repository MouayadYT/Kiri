using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch.Routing;
using Assistant.Core.Tools;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.Search;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The calculator and the tools that act on Windows, as the app puts them together (PROJECT_SPEC §4.1, §4.8, steps 102 and 103): the
/// bar's arithmetic is worked out by the real calculator, a tool that changes something is not run without the user's say-so, and the real
/// executor holds every call to the rules.
/// </summary>
public sealed class SystemToolsAppTests
{
    private static ToolCall Call(string name, string arguments = "{}") => new("c1", name, arguments);

    [Theory]
    [InlineData("9+10", "9 + 10", "19")]
    [InlineData("what is 12 * 3", "12 * 3", "36")]
    [InlineData("calculate (2 + 3) * 4", "(2 + 3) * 4", "20")]
    [InlineData("2^10", "2 ^ 10", "1024")]
    [InlineData("1,000 + 250", "1000 + 250", "1250")]
    [InlineData("0.1 + 0.2", "0.1 + 0.2", "0.3")]
    public async Task ASumTypedInTheBarIsWorkedOutByTheRealCalculator_AndShownAsTheCalculationCard(string typed, string expression, string value)
    {
        using var host = AppHost.Create();
        var answers = host.Services.GetRequiredService<CalculationAnswers>();

        var answer = await answers.TryAnswerAsync(typed, null, CancellationToken.None);

        Assert.NotNull(answer);
        var card = Assert.Single(answer.Content.OfType<CalculationResult>());
        Assert.Equal((expression, value), (card.Expression, card.Result));
        Assert.NotNull(card.CopyCommand);
        Assert.Equal($"{expression} is {value}.", answer.Text);
    }

    [Fact]
    public async Task ARoundedSumCarriesItsNote_AndASumThatHasNoValueIsNoAnswerAtAll()
    {
        using var host = AppHost.Create();
        var answers = host.Services.GetRequiredService<CalculationAnswers>();

        var third = await answers.TryAnswerAsync("1/3", null, CancellationToken.None);
        var zero = await answers.TryAnswerAsync("1/0", null, CancellationToken.None);

        Assert.Equal("Rounded to 10 decimal places.", Assert.Single(third!.Content.OfType<CalculationResult>()).Secondary);
        Assert.Null(zero);
    }

    [Fact]
    public void WhatIsNotArithmeticIsNotRoutedToTheCalculator()
    {
        using var host = AppHost.Create();
        var router = host.Services.GetRequiredService<IQueryRouter>();

        foreach (var typed in new[] { "brave", "downloads", "2024-10-02", "555-123-4567", "version 1.2.3", "what is the capital of France", "3 apples + 4 pears" })
        {
            Assert.NotEqual(QueryRouteKind.Calculation, router.Route(typed).Kind);
        }
    }

    [Fact]
    public async Task TheRealExecutorWorksASumOut_AndReturnsTheStructuredResult()
    {
        using var host = AppHost.Create();
        var executor = host.Services.GetRequiredService<IToolExecutor>();

        var result = await executor.ExecuteAsync(Call("calculate", """{"expression":"(3 + 4) * 5"}"""), new ToolContext(Guid.NewGuid()));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.True(CalculationToolResults.TryRead(result.OutputJson, out var output));
        Assert.Equal(new CalculationOutput("(3 + 4) * 5", "35"), output);
    }

    [Fact]
    public async Task WhileNothingShowsTheQuestion_NoToolThatChangesSomethingRuns_AndNothingIsDone()
    {
        using var host = AppHost.Create();

        // The real confirmation asks in the conversation's window. Here no window holds the conversation, so there is no one to ask, and a call that
        // cannot be asked about is not made: the same guarantee as the stand-in this replaced, now from the real thing.
        Assert.IsType<ConfirmationBroker>(host.Services.GetRequiredService<IPermissionService>());
        var registry = host.Services.GetRequiredService<IToolRegistry>();
        var executor = host.Services.GetRequiredService<IToolExecutor>();
        // The few small switches that are never asked about (the sound's on and off, Do not disturb, a note to remember) are named here and never run by this
        // test: on this host they would really mute the PC of whoever runs it.
        Assert.Equal(
            ["mute", "remember", "set_do_not_disturb", "unmute"],
            registry.Tools.Where(tool => tool.RunsWithoutAsking).Select(tool => tool.Name).Order());
        var sideEffects = registry.Tools.Where(tool => tool.RiskLevel == RiskLevel.SideEffect && !tool.RunsWithoutAsking).ToList();
        Assert.Equal(
            [
                "control_home_device", "control_timer", "open_application", "open_file", "open_folder", "remember_person", "reveal_file", "send_message",
                "set_alarm", "set_clock_display", "set_volume", "start_focus_session", "start_timer", "stopwatch", "take_screenshot",
            ],
            sideEffects.Select(tool => tool.Name).Order());

        var arguments = new Dictionary<string, string>
        {
            ["open_application"] = """{"application":"Calculator"}""",
            ["open_file"] = """{"file":"f1"}""",
            ["reveal_file"] = """{"file":"f1"}""",
            ["open_folder"] = """{"folder":"downloads"}""",
            ["set_volume"] = """{"percent":10}""",

            // The clock tools with what they would really be asked: nothing may reach the Clock app without the user's yes.
            ["set_alarm"] = """{"time":"16:00"}""",
            ["start_timer"] = """{"minutes":5}""",
            ["control_timer"] = """{"action":"stop"}""",
            ["stopwatch"] = """{"action":"start"}""",
            ["start_focus_session"] = """{"minutes":25}""",

            // The home tool with what it would really be asked: nothing may reach a Home Assistant without the user's yes.
            ["control_home_device"] = """{"device":"window fan","action":"turn_on"}""",
        };
        // This host reads the settings of whoever runs the test. An action they answered "Always allow" for would really be done here (a timer started,
        // the volume set), so it is left out; that such an action runs without the question, and that a message never does, is StandingApprovalTests'.
        var permissions = (await host.Services.GetRequiredService<ISettingsService>().LoadAsync()).Permissions;
        foreach (var tool in sideEffects.Where(tool => !StandingApprovals.IsKept(permissions, tool)))
        {
            var result = await executor.ExecuteAsync(Call(tool.Name, arguments.GetValueOrDefault(tool.Name, "{}")), new ToolContext(Guid.NewGuid()));

            // A file the conversation does not know fails first; the others are declined. Either way, nothing ran.
            Assert.NotEqual(ToolResultStatus.Succeeded, result.Status);
        }
    }

    [Fact]
    public async Task TheRealExecutorRefusesWhatADefinitionDoesNotAllow()
    {
        using var host = AppHost.Create();
        var executor = host.Services.GetRequiredService<IToolExecutor>();
        var context = new ToolContext(Guid.NewGuid());

        // Tools that need no permission, so that what the user has turned off in Settings cannot change what this checks.
        foreach (var (name, arguments) in new[]
                 {
                     ("calculate", """{"expression":"1+1","extra":true}"""),
                     ("get_volume", """{"device":"x"}"""),
                     ("set_volume", """{"percent":500}"""),
                     ("open_application", """{"application":"notepad","arguments":"x"}"""),
                     ("mute", """{"now":true}"""),
                 })
        {
            var result = await executor.ExecuteAsync(Call(name, arguments), context);

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.True(ToolErrors.TryRead(result.OutputJson, out var code, out _), result.OutputJson);
            Assert.Equal(ToolErrors.InvalidArguments, code);
        }
    }

    [Fact]
    public async Task TheRealExecutorReadsTheVolume_WithNothingAskedOfTheUser()
    {
        // Read-only: the speakers' own volume through Windows Core Audio. A PC with no sound output says so, in words.
        using var host = AppHost.Create();
        var executor = host.Services.GetRequiredService<IToolExecutor>();

        var result = await executor.ExecuteAsync(Call("get_volume"), new ToolContext(Guid.NewGuid()));

        if (result.Status == ToolResultStatus.Succeeded)
        {
            using var json = JsonDocument.Parse(result.OutputJson);
            Assert.InRange(json.RootElement.GetProperty("volume").GetInt32(), 0, 100);
            Assert.True(json.RootElement.TryGetProperty("muted", out _));
        }
        else
        {
            Assert.True(ToolErrors.TryRead(result.OutputJson, out _, out var message));
            Assert.Contains("no sound output", message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryToolTheModelIsOfferedHasItsDefinitionFixedInCode()
    {
        using var host = AppHost.Create();
        var registry = host.Services.GetRequiredService<IToolRegistry>();

        // Offered in a conversation with no screenshot: every tool but the one that reads a screenshot, but the two that read a calendar, which are offered only
        // while the app has a calendar to read (step 111), and it has none, but the two that message someone, which are offered only while the app has a
        // messaging provider (step 113), and it has none, and but the two for the home, which are offered only while a Home Assistant is known to be connected.
        var offered = registry.ToolsFor(new ToolContext(Guid.NewGuid())).Select(tool => tool.Name).ToList();

        Assert.DoesNotContain("read_screen_text", offered);
        Assert.DoesNotContain("get_calendar_events", offered);
        Assert.DoesNotContain("search_calendar_events", offered);
        Assert.DoesNotContain("draft_message", offered);
        Assert.DoesNotContain("send_message", offered);
        Assert.DoesNotContain("remember_person", offered);
        Assert.DoesNotContain("search_web", offered);
        Assert.DoesNotContain("control_home_device", offered);
        Assert.DoesNotContain("get_home_devices", offered);
        Assert.Contains("take_screenshot", offered);
        Assert.Equal(registry.Tools.Count - 9, offered.Count);
        Assert.All(registry.Tools, tool => Assert.True(tool.EffectiveTimeout <= ToolDefinition.MaxTimeout));
    }
}
