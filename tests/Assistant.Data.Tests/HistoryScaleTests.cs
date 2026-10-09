using System.Diagnostics;
using Assistant.Data.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Assistant.Data.Tests;

/// <summary>
/// A long history stays quick to list and to search: thousands of conversations, tens of thousands of messages. The
/// limits are generous, so they fail on work that grows with the square of the history, not on a slow machine.
/// </summary>
public sealed class HistoryScaleTests(ITestOutputHelper output) : IDisposable
{
    private const int Conversations = 2_000;
    private const int MessagesEach = 10;

    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(4);

    private static readonly string[] Words =
    [
        "weather", "pasta", "invoice", "flight", "recipe", "meeting", "budget", "garden", "camera", "python", "holiday", "guitar",
        "kitchen", "marathon", "battery", "printer", "library", "mortgage", "bicycle", "concert",
    ];

    private readonly HistoryHarness _history = new();

    public void Dispose() => _history.Dispose();

    private void Fill()
    {
        using var connection = _history.Open();
        _history.Service.InitializeAsync().GetAwaiter().GetResult();
        using var transaction = connection.BeginTransaction();
        var random = new Random(7);
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var conversation = 0; conversation < Conversations; conversation++)
        {
            var id = Guid.NewGuid().ToString("D");
            var at = DatabaseTimestamps.ToText(start.AddMinutes(conversation * 30));
            connection.Execute(
                "INSERT INTO conversations (id, title, created_at, updated_at) VALUES ($id, $title, $at, $at)",
                ("$id", id),
                ("$title", $"Conversation about {Words[conversation % Words.Length]} number {conversation}"),
                ("$at", at));
            for (var position = 0; position < MessagesEach; position++)
            {
                var text = string.Join(' ', Enumerable.Range(0, 60).Select(_ => Words[random.Next(Words.Length)])) +
                    (conversation == 1234 && position == 3 ? " needlemarker" : string.Empty);
                connection.Execute(
                    "INSERT INTO messages (id, conversation_id, position, role, text, created_at) VALUES ($m, $c, $p, $r, $t, $at)",
                    ("$m", Guid.NewGuid().ToString("D")),
                    ("$c", id),
                    ("$p", position),
                    ("$r", position % 2 == 0 ? "User" : "Assistant"),
                    ("$t", text),
                    ("$at", at));
            }
        }

        transaction.Commit();
    }

    [Fact]
    public async Task ALongHistoryIsListedAndSearchedQuickly()
    {
        Fill();
        var service = _history.Service;

        var listed = await Timed("list", () => service.ListAsync());
        Assert.Equal(Conversations, listed.Count);
        Assert.Equal("Conversation about " + Words[(Conversations - 1) % Words.Length] + " number " + (Conversations - 1), listed[0].Title);

        var rare = await Timed("rare word", () => service.SearchAsync("needlemarker"));
        Assert.Single(rare);

        var common = await Timed("common word", () => service.SearchAsync("weather"));
        Assert.Equal(IConversationServiceLimit, common.Count);

        var two = await Timed("two words", () => service.SearchAsync("weather pasta"));
        Assert.NotEmpty(two);

        var prefix = await Timed("prefix", () => service.SearchAsync("wea"));
        Assert.NotEmpty(prefix);

        var inside = await Timed("word inside a word", () => service.SearchAsync("eedlemark"));
        Assert.Single(inside);

        var none = await Timed("nothing", () => service.SearchAsync("nosuchwordanywhere"));
        Assert.Empty(none);

        var loaded = await Timed("open one", () => service.GetAsync(listed[0].Id));
        Assert.NotNull(loaded);
        Assert.Equal(MessagesEach, loaded.Messages.Count);
    }

    private const int IConversationServiceLimit = 100;

    private async Task<T> Timed<T>(string what, Func<Task<T>> work)
    {
        var watch = Stopwatch.StartNew();
        var result = await work();
        watch.Stop();
        output.WriteLine($"{what}: {watch.ElapsedMilliseconds} ms");
        Assert.True(watch.Elapsed < Limit, $"'{what}' took {watch.Elapsed.TotalSeconds:0.0} s.");
        return result;
    }
}
