using System.Text.Json;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;
using Assistant.Tools.Time;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// <c>get_time</c> (PROJECT_SPEC §4.8): asked what time it was, the model said it had no way to know. It reads the PC's clock with this tool, in the
/// PC's own time zone, without anyone being asked, since it changes nothing.
/// </summary>
public sealed class TimeToolsTests
{
    // A clock that stands at one moment, in a zone of its own, whatever the PC that runs the test says.
    private sealed class StoppedClock(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();

        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private static readonly TimeZoneInfo ThreeHoursAhead = TimeZoneInfo.CreateCustomTimeZone("Test/East", TimeSpan.FromHours(3), "East", "Eastern Test Time");

    [Fact]
    public async Task TheTimeTheDateTheDayAndTheZoneAreThePCsOwn()
    {
        var clock = new StoppedClock(new DateTimeOffset(2026, 10, 7, 13, 42, 5, TimeSpan.Zero), ThreeHoursAhead);
        var tool = TimeTools.GetTime(clock);

        var result = await tool.RunAsync(new ToolCall("c1", TimeTools.GetTimeName, "{}"), default, new ToolContext(Guid.NewGuid(), "what time is it"), default);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        using var said = JsonDocument.Parse(result.OutputJson);
        Assert.Equal("16:42", said.RootElement.GetProperty("time").GetString());
        Assert.Equal("2026-10-07", said.RootElement.GetProperty("date").GetString());
        Assert.Equal("Wednesday", said.RootElement.GetProperty("day").GetString());
        Assert.Equal("Eastern Test Time (UTC+03:00)", said.RootElement.GetProperty("time_zone").GetString());
        Assert.False(string.IsNullOrWhiteSpace(said.RootElement.GetProperty("as_the_user_writes_it").GetString()));
    }

    [Fact]
    public void AZoneBehindUtcIsWrittenWithItsSign()
    {
        var west = TimeZoneInfo.CreateCustomTimeZone("Test/West", TimeSpan.FromHours(-5.5), "West", "Western Test Time");
        using var said = JsonDocument.Parse(TimeTools.Describe(new DateTimeOffset(2026, 1, 1, 0, 5, 0, TimeSpan.FromHours(-5.5)), west));

        Assert.Equal("00:05", said.RootElement.GetProperty("time").GetString());
        Assert.Equal("Western Test Time (UTC-05:30)", said.RootElement.GetProperty("time_zone").GetString());
    }

    [Fact]
    public void ItIsOfferedInEveryRequest_ReadsOnly_AndTakesNothing()
    {
        var tool = TimeTools.GetTime(TimeProvider.System);

        Assert.True(tool.IsOffered(new ToolContext(Guid.NewGuid(), "what is the capital of France")));
        Assert.True(tool.IsOffered(new ToolContext(Guid.NewGuid(), "what time is it")));
        Assert.Equal(RiskLevel.ReadOnly, tool.Definition.RiskLevel);
        using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);
        Assert.Equal("object", schema.RootElement.GetProperty("type").GetString());
        Assert.Contains("Never say you cannot know the time", tool.Definition.Description, StringComparison.Ordinal);
    }
}
