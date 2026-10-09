using Assistant.Core.Domain;
using Assistant.Data.Persistence;
using Xunit;
using static Assistant.Data.Tests.HistoryHarness;

namespace Assistant.Data.Tests;

/// <summary>What a person said is private (PROJECT_SPEC §3.2): the history never puts it in a log or in an error message.</summary>
public sealed class HistoryPrivacyTests : IDisposable
{
    private const string TitleSecret = "the-secret-title-words";
    private const string MessageSecret = "the-secret-message-words";
    private const string QuerySecret = "the-secret-query-words";
    private const string PathSecret = "the-secret-folder";

    private readonly HistoryHarness _history = new();

    public void Dispose() => _history.Dispose();

    [Fact]
    public async Task NoOperationLogsATitleAMessageAQueryOrAPath()
    {
        var service = _history.Service;
        var conversation = Guid.NewGuid();
        var photo = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "photo.png") { FilePath = $@"C:\{PathSecret}\photo.png" };

        await _history.SayAsync(conversation, User($"{MessageSecret} and {TitleSecret}", 0, photo), Answer($"Answer about {MessageSecret}", 1));
        await service.SaveAsync(new Conversation(Guid.NewGuid(), TitleSecret, At(0), At(1)) { Messages = [User(MessageSecret, 0)] });
        await service.ListAsync();
        await service.GetAsync(conversation);
        await service.SearchAsync($"{QuerySecret} {MessageSecret}");
        await service.SearchAsync(MessageSecret);
        await service.RenameAsync(conversation, TitleSecret + " again");
        await service.DeleteAsync(conversation);
        await service.DeleteAllAsync();

        var text = _history.Logs.AllText;
        Assert.Contains("History saved a message", text, StringComparison.Ordinal);
        Assert.Contains("History searched", text, StringComparison.Ordinal);
        Assert.DoesNotContain(TitleSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(MessageSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(QuerySecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(PathSecret, text, StringComparison.Ordinal);
        Assert.DoesNotContain("photo.png", text, StringComparison.Ordinal);
        Assert.DoesNotContain(_history.Database.Root, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AFailureIsLoggedByItsTypeAlone_AndItsMessageHoldsNoContent()
    {
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("First", 0));
        using (var connection = _history.Open())
        {
            connection.Execute($"CREATE TRIGGER refuse BEFORE INSERT ON messages BEGIN SELECT RAISE(ABORT, '{MessageSecret}'); END");
        }

        var failure = await Assert.ThrowsAsync<DatabaseException>(
            () => _history.Service.SaveMessageAsync(conversation, User(MessageSecret, 1), At(1)));

        var text = _history.Logs.AllText;
        Assert.Contains("SqliteException", text, StringComparison.Ordinal);
        Assert.DoesNotContain(MessageSecret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(MessageSecret, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDomainTypesKeepTheirContentOutOfToString()
    {
        var conversation = Guid.NewGuid();
        var photo = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "photo.png") { FilePath = $@"C:\{PathSecret}\photo.png" };
        await _history.SayAsync(conversation, User(MessageSecret, 0, photo), Answer(MessageSecret, 1, MessageOutcome.Complete, new CardMetadata("code", $"{{\"code\":\"{MessageSecret}\"}}", 0)));

        var summary = Assert.Single(await _history.Service.ListAsync());
        var result = Assert.Single(await _history.Service.SearchAsync(MessageSecret));
        var loaded = await _history.Service.GetAsync(conversation);

        Assert.NotNull(loaded);
        Assert.All(
            new object?[] { summary, result, result.Snippet, loaded, loaded.Messages[0], loaded.Messages[1], loaded.Messages[1].Cards[0], summary.LatestImage },
            value =>
            {
                var text = value!.ToString()!;
                Assert.DoesNotContain(MessageSecret, text, StringComparison.Ordinal);
                Assert.DoesNotContain(PathSecret, text, StringComparison.Ordinal);
                Assert.DoesNotContain("photo.png", text, StringComparison.Ordinal);
            });
    }
}
