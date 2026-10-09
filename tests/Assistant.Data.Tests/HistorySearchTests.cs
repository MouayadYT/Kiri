using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Data.Persistence;
using Xunit;
using static Assistant.Data.Tests.HistoryHarness;

namespace Assistant.Data.Tests;

/// <summary>Searching the saved conversations' titles and messages on the device.</summary>
public sealed class HistorySearchTests : IDisposable
{
    private readonly HistoryHarness _history = new();

    private readonly Guid _weather = Guid.NewGuid();
    private readonly Guid _pasta = Guid.NewGuid();
    private readonly Guid _cafe = Guid.NewGuid();

    public HistorySearchTests()
    {
        // The weather conversation asks in one message and is answered in another; the pasta one is older; the third has
        // an accent and a name of its own.
        _history.SayAsync(_pasta, User("What can I cook with pasta, spinach and garlic?", 0), Answer("Try a garlicky spinach pasta: wilt the spinach, then toss it with lemon.", 1)).GetAwaiter().GetResult();
        _history.SayAsync(_weather, User("What’s the weather this weekend?", 10), Answer("Saturday looks **sunny** with a high of 22°, and Sunday brings light rain.", 11)).GetAwaiter().GetResult();
        _history.SayAsync(_cafe, User("Where should we have dinner?", 20), Answer("Book a table at the Café Rouge, it is lovely.", 21)).GetAwaiter().GetResult();
    }

    private SqliteConversationService Service => _history.Service;

    public void Dispose() => _history.Dispose();

    private async Task<Guid[]> FindAsync(string query, int limit = IConversationService.DefaultSearchLimit) =>
        [.. (await Service.SearchAsync(query, limit)).Select(result => result.Conversation.Id)];

    [Fact]
    public async Task AWordInAMessageFindsItsConversation()
    {
        Assert.Equal([_weather], await FindAsync("sunny"));
        Assert.Equal([_pasta], await FindAsync("garlic"));
    }

    [Fact]
    public async Task AWordIsFoundByHowItBegins_WhileItIsStillBeingTyped()
    {
        Assert.Equal([_weather], await FindAsync("wea"));
        Assert.Equal([_pasta], await FindAsync("spin"));
    }

    [Fact]
    public async Task MatchingIgnoresCase()
    {
        Assert.Equal([_weather], await FindAsync("SUNNY"));
        Assert.Equal([_weather], await FindAsync("SuNnY"));
    }

    [Fact]
    public async Task MatchingIgnoresAccents_InBothDirections()
    {
        Assert.Equal([_cafe], await FindAsync("cafe"));
        Assert.Equal([_cafe], await FindAsync("CAFÉ"));
        Assert.Equal([_cafe], await FindAsync("café"));
    }

    [Fact]
    public async Task EveryWordMustBeFound_ButNotInTheSameMessage()
    {
        // "weather" is in the question, "sunny" in the answer.
        Assert.Equal([_weather], await FindAsync("weather sunny"));
        Assert.Equal([_weather], await FindAsync("sunny weather"));
        Assert.Empty(await FindAsync("weather pasta"));
        Assert.Empty(await FindAsync("sunny zzzunknown"));
    }

    [Fact]
    public async Task APhraseInQuotesIsFoundOnlyWhenItsWordsAreTogetherInOrder()
    {
        Assert.Equal([_weather], await FindAsync("\"light rain\""));
        Assert.Equal([_weather], await FindAsync("\"light ra\""));
        Assert.Empty(await FindAsync("\"rain light\""));
        Assert.Empty(await FindAsync("\"weather sunny\""));
    }

    [Fact]
    public async Task ATitleIsSearchedToo_AndAConversationFoundByItsTitleAloneHasNoSnippet()
    {
        await Service.RenameAsync(_pasta, "Italian night");

        var results = await Service.SearchAsync("italian");

        var result = Assert.Single(results);
        Assert.Equal(_pasta, result.Conversation.Id);
        Assert.Equal("Italian night", result.Conversation.Title);
        Assert.Null(result.Snippet);
    }

    [Fact]
    public async Task ARenamedConversationIsFoundByItsNewTitle_NotTheOldOne()
    {
        var title = "Cook something quickly";
        await Service.RenameAsync(_pasta, title);
        Assert.Equal([_pasta], await FindAsync("quickly"));

        await Service.RenameAsync(_pasta, "Something else entirely");

        Assert.Empty(await FindAsync("quickly"));
        Assert.Equal([_pasta], await FindAsync("entirely"));
    }

    [Fact]
    public async Task AResultShowsTheWordsAroundTheMatchInTheLatestMessageThatHasOne()
    {
        var result = Assert.Single(await Service.SearchAsync("sunny"));

        Assert.NotNull(result.Snippet);
        Assert.Equal("Saturday looks sunny with a high of 22°, and Sunday brings light rain.", result.Snippet.Text);
        var match = Assert.Single(result.Snippet.Matches);
        Assert.Equal("sunny", result.Snippet.Text.Substring(match.Start, match.Length));

        // It names the message the words are in, which is the answer.
        var conversation = await Service.GetAsync(_weather);
        Assert.NotNull(conversation);
        Assert.Equal(conversation.Messages[1].Id, result.Snippet.MessageId);
    }

    [Fact]
    public async Task WhenSeveralMessagesMatch_TheSnippetComesFromTheLatestOne()
    {
        var question = User("Any pasta ideas tonight?", 30);
        var answer = Answer("Pasta primavera is quick.", 31);
        await _history.SayAsync(_pasta, question, answer);

        var result = Assert.Single(await Service.SearchAsync("pasta"));

        Assert.NotNull(result.Snippet);
        Assert.Equal(answer.Id, result.Snippet.MessageId);
    }

    [Fact]
    public async Task ResultsAreNewestFirst_AndLimited()
    {
        var conversations = new List<Guid>();
        for (var index = 0; index < 6; index++)
        {
            var id = Guid.NewGuid();
            conversations.Add(id);
            await _history.SayAsync(id, User($"Trip number {index} to the coast", 100 + index));
        }

        var all = await FindAsync("coast");
        var two = await FindAsync("coast", limit: 2);

        Assert.Equal(Enumerable.Reverse(conversations), all);
        Assert.Equal(all.Take(2), two);
        Assert.Empty(await FindAsync("coast", limit: 0));
    }

    [Fact]
    public async Task AWordInsideAnotherWordIsFound_WhenNoWordBeginsWithIt()
    {
        // No word begins with "unn", but "sunny" has it inside.
        var result = Assert.Single(await Service.SearchAsync("unn"));

        Assert.Equal(_weather, result.Conversation.Id);
        Assert.NotNull(result.Snippet);
        var match = Assert.Single(result.Snippet.Matches);
        Assert.Equal("unn", result.Snippet.Text.Substring(match.Start, match.Length));
    }

    [Fact]
    public async Task AWordInsideAnotherWordIsNotLookedFor_WhenSomeWordBeginsWithIt()
    {
        var unsung = Guid.NewGuid();
        await _history.SayAsync(unsung, User("An unsung hero", 40));

        // "sun" begins "sunny" in one conversation and only sits inside "unsung" in the other.
        Assert.Equal([_weather], await FindAsync("sun"));
    }

    [Fact]
    public async Task TheSlowerPassFoldsCaseAndAccentsToo()
    {
        Assert.Equal([_cafe], await FindAsync("AFÉ R"));
        Assert.Equal([_cafe], await FindAsync("afe"));

        // The accent sits inside the phrase: only text with its accents taken off contains it.
        Assert.Equal([_cafe], await FindAsync("\"afe rou\""));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("?")]
    [InlineData("\"\"")]
    public async Task AQueryWithNoWordsFindsNothing(string query)
    {
        Assert.Empty(await Service.SearchAsync(query));
    }

    [Theory]
    [InlineData("\"unterminated phrase")]
    [InlineData("C++")]
    [InlineData("*")]
    [InlineData("NEAR(pasta spinach)")]
    [InlineData("pasta AND")]
    [InlineData("OR NOT")]
    [InlineData("title:pasta")]
    [InlineData("pasta -spinach")]
    [InlineData("(pasta")]
    [InlineData("pasta)")]
    [InlineData("^pasta")]
    [InlineData("it's")]
    [InlineData("a\\b")]
    [InlineData("100% sure_")]
    [InlineData("' OR 1=1 --")]
    [InlineData("'; DROP TABLE messages; --")]
    [InlineData("\"; DROP TABLE messages; --")]
    public async Task WhatWasTypedIsAlwaysJustWords_NeverSyntax(string query)
    {
        // None of it raises an error or changes the database.
        await Service.SearchAsync(query);

        using var connection = _history.Open();
        Assert.Equal(6, connection.Count("messages"));
        Assert.Equal(3, connection.Count("conversations"));
    }

    [Fact]
    public async Task OperatorWordsAreSearchedForAsWords()
    {
        await _history.SayAsync(Guid.NewGuid(), User("This or that", 50));

        // "or" is a word to find, not an operator that would match everything.
        var found = await FindAsync("or");

        Assert.Single(found);
    }

    [Fact]
    public async Task ANonLatinWordIsFound()
    {
        var arabic = Guid.NewGuid();
        await _history.SayAsync(arabic, User("ما هو الطقس اليوم؟", 60), Answer("الطقس مشمس اليوم", 61));

        Assert.Equal([arabic], await FindAsync("الطقس"));
        Assert.Equal([arabic], await FindAsync("مشمس"));
    }

    [Fact]
    public async Task EditingAMessageChangesWhatFindsIt()
    {
        var conversation = Guid.NewGuid();
        var answer = Answer("The zebra crossed the road", 71);
        await _history.SayAsync(conversation, User("Story", 70), answer);
        Assert.Equal([conversation], await FindAsync("zebra"));

        await Service.SaveMessageAsync(conversation, answer with { Text = "The giraffe crossed the road" }, At(72));

        Assert.Empty(await FindAsync("zebra"));
        Assert.Equal([conversation], await FindAsync("giraffe"));
    }

    [Fact]
    public async Task AWholeSavedConversationIsSearchable_AndReplacingItForgetsTheOldWords()
    {
        var conversation = Guid.NewGuid();
        await Service.SaveAsync(new Conversation(conversation, "Imported", At(80), At(81))
        {
            Messages = [User("Kangaroo facts please", 80), Answer("Kangaroos hop.", 81)],
        });
        Assert.Equal([conversation], await FindAsync("kangaroo"));

        await Service.SaveAsync(new Conversation(conversation, "Imported", At(80), At(83))
        {
            Messages = [User("Koala facts please", 82)],
        });

        Assert.Empty(await FindAsync("kangaroo"));
        Assert.Equal([conversation], await FindAsync("koala"));
    }

    [Fact]
    public async Task AConversationDeletedIsNoLongerFound()
    {
        await Service.DeleteAsync(_weather);

        Assert.Empty(await FindAsync("sunny"));
        Assert.Empty(await FindAsync("weather"));
        Assert.Equal([_pasta], await FindAsync("spinach"));
    }

    [Fact]
    public async Task ResultsCarryWhatACardShows()
    {
        var photo = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "sunset.jpg") { FilePath = @"C:\p\sunset.jpg" };
        var conversation = Guid.NewGuid();
        await _history.SayAsync(conversation, User("Describe this sunset", 90, photo), Answer("Warm colors over the sea.", 91));

        var result = Assert.Single(await Service.SearchAsync("sunset"));

        Assert.Equal("sunset.jpg", result.Conversation.LatestImage?.DisplayName);
        Assert.Equal("Warm colors over the sea.", result.Conversation.LatestAnswerText);
        Assert.Equal(2, result.Conversation.MessageCount);
    }

    [Fact]
    public async Task SearchingNeedsNoModelAndNoNetwork()
    {
        // The search is a method of the history over the local database: nothing it is built from can ask a model or reach
        // the network.
        var constructor = Assert.Single(typeof(SqliteConversationService).GetConstructors());
        Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(IModelService));
        var references = typeof(SqliteConversationService).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
        // The assembly also contains the model downloader; history itself takes no network client.
        Assert.DoesNotContain(constructor.GetParameters(), parameter => parameter.ParameterType == typeof(HttpClient));
        Assert.DoesNotContain("System.Net.Sockets", references);
        Assert.DoesNotContain(references, name => name!.StartsWith("Assistant.ModelHost", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ASearchLeavesTheDatabaseAsItWas()
    {
        var before = DatabaseState();

        await FindAsync("weather sunny");
        await FindAsync("nomatchanywhere");
        await FindAsync("unn");

        Assert.Equal(before, DatabaseState());
    }

    private (int Messages, int Conversations, string Newest) DatabaseState()
    {
        using var connection = _history.Open();
        return (
            connection.Count("messages"),
            connection.Count("conversations"),
            connection.ExecuteScalar<string>("SELECT MAX(updated_at) FROM conversations") ?? string.Empty);
    }
}
