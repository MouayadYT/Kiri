using Assistant.Core.Budgeting;
using Assistant.Core.Clock;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Assistant.Core.Tools;
using Xunit;

namespace Assistant.Core.Tests;

/// <summary>
/// What a connected app's tools are told about today (so "due today" needs no question about a date or a time zone), the Cleanup settings and
/// their limits, and the place of the Clock window as the user rewrites it in Settings.
/// </summary>
public sealed class CleanupAndDatesTests
{
    private sealed class At(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    [Fact]
    public void AConnectedAppsToolsAreToldTheDayAndThePcsTimeZoneSoThatNeitherIsAsked()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var now = new DateTimeOffset(2026, 10, 8, 16, 30, 0, TimeSpan.FromHours(-4));

        var words = AssistantInstructions.ConnectedAppDates(now, zone);

        Assert.Contains("today is Thursday 2026-10-08", words, StringComparison.Ordinal);
        Assert.Contains("Eastern Standard Time", words, StringComparison.Ordinal);
        Assert.Contains("America/New_York", words, StringComparison.Ordinal);
        Assert.Contains("UTC-04:00", words, StringComparison.Ordinal);
        Assert.Contains("do not ask", words, StringComparison.Ordinal);
        Assert.Contains("end of today", words, StringComparison.Ordinal);

        // The day and not the minute: the same words all day, so the prompt can be kept.
        Assert.Equal(words, AssistantInstructions.ConnectedAppDates(now.AddHours(3), zone));
        Assert.True(words.Length < 320, "It takes room in a small model's window.");
    }

    [Fact]
    public void TheDatesFollowTheGuidanceOnlyInARequestThatIsOfferedAConnectedAppsTool()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var clock = new At(new DateTimeOffset(2026, 10, 8, 16, 30, 0, TimeSpan.FromHours(-4)), zone);
        var builder = new PromptBuilder(new ContextService(new ContextBudgeter(new HeuristicTokenEstimator())), clock);
        var conversation = new[] { new Message(Guid.NewGuid(), MessageRole.User, "add homework to my to do, due today", DateTimeOffset.UtcNow) };
        var model = new ModelInfo("tool-model", 8192) { SupportsToolCalling = true };
        static ToolDefinition Tool(string name) => new(name, "Does something.", "{\"type\":\"object\",\"properties\":{}}", RiskLevel.ReadOnly);

        var withApp = builder.Build(null, conversation, model, null, Guid.NewGuid(), [Tool("mcp_microsofttodo_create_task")]).Request.Instructions;
        var without = builder.Build(null, conversation, model, null, Guid.NewGuid(), [Tool("search_files")]).Request.Instructions;

        Assert.Contains("today is Thursday 2026-10-08", withApp, StringComparison.Ordinal);
        Assert.Contains("never ask them in words to confirm", withApp, StringComparison.Ordinal);
        Assert.Contains("never open the app", withApp, StringComparison.Ordinal);
        Assert.DoesNotContain("today is Thursday", without, StringComparison.Ordinal);
    }

    [Fact]
    public void CleanupDeletesNothingUntilTheUserTurnsItOn_AndItsTimesAreTheOnesAskedFor()
    {
        var cleanup = new AppSettings().Cleanup;

        Assert.False(cleanup.DeleteBarChats);
        Assert.False(cleanup.DeleteWindowChats);
        Assert.False(cleanup.DeletesAnything);
        Assert.Equal((10, 7), (cleanup.BarChatHours, cleanup.WindowChatDays));
        Assert.Empty(SettingsValidator.Validate(new AppSettings()));
    }

    [Theory]
    [InlineData(0, 7, "Cleanup.BarChatHours")]
    [InlineData(9000, 7, "Cleanup.BarChatHours")]
    [InlineData(10, 0, "Cleanup.WindowChatDays")]
    [InlineData(10, 4000, "Cleanup.WindowChatDays")]
    public void ACleanupTimeThatCannotBeIsPutBackToItsDefault_AndTheSwitchesAreLeftAsTheyWere(int hours, int days, string named)
    {
        var settings = new AppSettings { Cleanup = new CleanupSettings { DeleteBarChats = true, BarChatHours = hours, DeleteWindowChats = true, WindowChatDays = days } };

        var repaired = SettingsValidator.Sanitize(settings, out var issues);

        Assert.Equal(named, Assert.Single(issues).Setting);
        Assert.Equal((10, 7), (Math.Min(repaired.Cleanup.BarChatHours, 10), Math.Min(repaired.Cleanup.WindowChatDays, 7)));
        Assert.True(repaired.Cleanup.DeleteBarChats && repaired.Cleanup.DeleteWindowChats);
        Assert.InRange(repaired.Cleanup.BarChatHours, SettingsLimits.MinCleanupHours, SettingsLimits.MaxCleanupHours);
        Assert.InRange(repaired.Cleanup.WindowChatDays, SettingsLimits.MinCleanupDays, SettingsLimits.MaxCleanupDays);
    }

    [Theory]
    [InlineData("94% across and 36% down", 0.94, 0.36)]
    [InlineData("36% down, 94% across", 0.94, 0.36)]
    [InlineData("12.5 across 0 down", 0.125, 0)]
    [InlineData("on Display 1, on the left, 100 30", 1, 0.3)]
    public void APlaceIsReadFromItsTwoPercentagesInEitherOrder_OrFromTheLastTwoNumbers(string typed, double across, double down)
    {
        Assert.True(ClockPlace.TryReadSpot(typed, out var spot));
        Assert.Equal((across, down), (spot.X, spot.Y));
    }

    [Theory]
    [InlineData("")]
    [InlineData("on the left")]
    [InlineData("50% across")]
    [InlineData("101% across and 20% down")]
    public void WhatHoldsNoTwoNumbersFromZeroToAHundredIsNotAPlace(string typed) => Assert.False(ClockPlace.TryReadSpot(typed, out _));

    [Fact]
    public void RewritingThePlaceKeepsTheDisplayThatWasChosen_AndAPlaceIsAddedToAnEntryThatHadNone()
    {
        var kept = new MemoryEntry(Guid.NewGuid(), MemoryKind.Preference, "The Clock window (alarms and timers) opens on Display 2, on the left.", DateTimeOffset.UtcNow)
        {
            Key = ClockPlace.Key,
            Value = @"\\.\DISPLAY2|left monitor",
        };

        var rewritten = ClockPlace.Rewritten(kept, "25% across and 75% down");

        Assert.NotNull(rewritten);
        Assert.Equal("The Clock window (alarms and timers) opens on Display 2, on the left, 25% across and 75% down.", rewritten.Text);
        Assert.Equal(@"\\.\DISPLAY2|left monitor|0.25|0.75", rewritten.Value);
        Assert.Equal((kept.Id, kept.Key, kept.Kind), (rewritten.Id, rewritten.Key, rewritten.Kind));
        Assert.Null(ClockPlace.Rewritten(kept, "a bit lower"));
    }
}