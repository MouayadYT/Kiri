using Assistant.Data.Persistence;
using Xunit;

namespace Assistant.Data.Tests;

public sealed class DatabaseTimestampsTests
{
    [Fact]
    public void WritesUtcIsoTextWithSevenFractionalDigits()
    {
        var text = DatabaseTimestamps.ToText(new DateTimeOffset(2026, 9, 30, 12, 15, 0, TimeSpan.FromHours(2)));

        Assert.Equal("2026-09-30T10:15:00.0000000Z", text);
    }

    [Fact]
    public void ReadsBackTheSameMomentInUtc()
    {
        var original = new DateTimeOffset(2026, 9, 30, 12, 15, 0, 123, TimeSpan.FromHours(-5)).AddTicks(4567);

        var read = DatabaseTimestamps.FromText(DatabaseTimestamps.ToText(original));

        Assert.Equal(original, read);
        Assert.Equal(TimeSpan.Zero, read.Offset);
    }

    [Fact]
    public void TextOrderIsTimeOrderAcrossOffsets()
    {
        var moments = new[]
        {
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(1),
            new DateTimeOffset(2026, 1, 1, 1, 30, 0, TimeSpan.FromHours(3)), // 2025-12-31 22:30 UTC
            new DateTimeOffset(2026, 9, 30, 23, 59, 59, TimeSpan.FromHours(-8)),
            new DateTimeOffset(2027, 2, 3, 4, 5, 6, TimeSpan.Zero),
        };

        var byTime = moments.OrderBy(moment => moment).Select(DatabaseTimestamps.ToText).ToArray();
        var byText = moments.Select(DatabaseTimestamps.ToText).Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(byTime, byText);
    }

    [Theory]
    [InlineData("")]
    [InlineData("yesterday")]
    [InlineData("2026-09-30T10:15:00Z")]
    [InlineData("2026-09-30T10:15:00.0000000+02:00")]
    public void RefusesText_ThatIsNotInTheDatabasesFormat(string text)
    {
        Assert.Throws<FormatException>(() => DatabaseTimestamps.FromText(text));
    }
}
