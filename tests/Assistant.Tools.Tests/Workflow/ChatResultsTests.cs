using System.Text.Json;
using Assistant.Tools.Mcp;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>Reading what a connected messaging app's search for chats answered (PROJECT_SPEC §4.8, step 116): the common shapes, and nothing guessed from the rest.</summary>
public sealed class ChatResultsTests
{
    private static McpToolResult Text(string text) => Sample.Text(text);

    [Fact]
    public void ChatsUnderANameAreRead()
    {
        var chats = ChatResults.Parse(Text("""{"chats":[{"id":"c1","title":"Omar","network":"WhatsApp","type":"single"},{"id":"c2","title":"Lina","network":"Signal"}]}"""));

        Assert.Equal(2, chats.Count);
        Assert.Equal("c1", chats[0].Id);
        Assert.Equal("Omar", chats[0].Title);
        Assert.Equal("WhatsApp", chats[0].Network);
        Assert.False(chats[0].IsGroup);
        Assert.Null(chats[1].IsGroup);
    }

    [Theory]
    [InlineData("""[{"id":"c1","name":"Omar"}]""")]
    [InlineData("""{"results":[{"chat_id":"c1","display_name":"Omar"}]}""")]
    [InlineData("""{"data":{"items":[{"chatId":"c1","subject":"Omar"}]}}""")]
    [InlineData("""{"id":"c1","title":"Omar"}""")]
    [InlineData("""{"conversations":[{"conversation_id":"c1","label":"Omar"}]}""")]
    [InlineData("""{"chats":[{"id":"c1","title":"Omar"}],"total":1}""")]
    public void TheShapesServersCommonlyUseAreRead(string json)
    {
        var chat = Assert.Single(ChatResults.Parse(Text(json)));

        Assert.Equal("c1", chat.Id);
        Assert.Equal("Omar", chat.Title);
    }

    [Fact]
    public void ANumericIdIsKeptAsText()
    {
        var chat = Assert.Single(ChatResults.Parse(Text("""[{"id":42,"title":"Omar"}]""")));

        Assert.Equal("42", chat.Id);
    }

    [Fact]
    public void StructuredContentIsReadToo()
    {
        var result = new McpToolResult(false, [], Sample.Json("""{"chats":[{"id":"c1","title":"Omar"}]}"""));

        Assert.Equal("c1", Assert.Single(ChatResults.Parse(result)).Id);
    }

    [Fact]
    public void TheSameChatInTwoPlacesIsOne()
    {
        var result = new McpToolResult(false, [new McpContentBlock(McpContentKind.Text, """{"chats":[{"id":"c1","title":"Omar"}]}""", null, null, null)], Sample.Json("""{"chats":[{"id":"c1","title":"Omar"}]}"""));

        Assert.Single(ChatResults.Parse(result));
    }

    [Fact]
    public void TheParticipantsOfAChatAreNotChats()
    {
        var chats = ChatResults.Parse(Text("""{"chats":[{"id":"c1","title":"Omar","participants":[{"id":"u1","name":"Omar"},{"id":"u2","name":"Me"}]}]}"""));

        Assert.Equal("c1", Assert.Single(chats).Id);
    }

    [Fact]
    public void AnObjectWithAnIdThatHoldsChatsIsAWrapperAndNotAChat()
    {
        var chats = ChatResults.Parse(Text("""{"id":"request-1","chats":[{"id":"c1","title":"Omar"}]}"""));

        Assert.Equal("c1", Assert.Single(chats).Id);
    }

    [Theory]
    [InlineData("""{"id":"c","type":"group"}""", true)]
    [InlineData("""{"id":"c","type":"channel"}""", true)]
    [InlineData("""{"id":"c","chat_type":"supergroup"}""", true)]
    [InlineData("""{"id":"c","is_group":true}""", true)]
    [InlineData("""{"id":"c","isGroup":true}""", true)]
    [InlineData("""{"id":"c","participant_count":5}""", true)]
    [InlineData("""{"id":"c","participants":[{"n":1},{"n":2},{"n":3}]}""", true)]
    [InlineData("""{"id":"c","type":"single"}""", false)]
    [InlineData("""{"id":"c","type":"direct"}""", false)]
    [InlineData("""{"id":"c","type":"dm"}""", false)]
    [InlineData("""{"id":"c","is_group":false}""", false)]
    [InlineData("""{"id":"c","participant_count":2}""", false)]
    [InlineData("""{"id":"c","participants":[{"n":1},{"n":2}]}""", false)]
    [InlineData("""{"id":"c"}""", null)]
    public void WhetherAChatIsAGroupIsReadFromWhatTheAppSays(string json, bool? expected)
    {
        Assert.Equal(expected, Assert.Single(ChatResults.Parse(Text(json))).IsGroup);
    }

    [Fact]
    public void EveryTextAndNumberInAChatIsAFactAboutItSoASavedNumberCanBeLookedFor()
    {
        var chat = Assert.Single(ChatResults.Parse(Text("""{"id":"c1","title":"Omar","participants":[{"phone":"+1 555 0100","note":"Brother"}],"unread":3}""")));

        Assert.Contains("15550100", chat.Facts.Where(char.IsLetterOrDigit).Aggregate(string.Empty, (text, character) => text + character), StringComparison.Ordinal);
        Assert.Contains("brother", chat.Facts, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Nothing found.")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("""{"chats":[]}""")]
    [InlineData("""{"chats":["a","b"]}""")]
    [InlineData("""{"message":"There are no chats"}""")]
    [InlineData("")]
    public void WhatIsNotUnderstoodListsNoChat(string text)
    {
        Assert.Empty(ChatResults.Parse(Text(text)));
    }

    // ---- An app that answers in words (Beeper tells the model how to use what it found) ----------------------------------------

    [Fact]
    public void JsonInsideWordsIsRead()
    {
        var chats = ChatResults.Parse(Text(
            "Found 1 chat. Use the chatID with send_message to write to it.\n\n[{\"id\":\"!abc:beeper.local\",\"title\":\"Savannah\",\"type\":\"single\",\"network\":\"Beeper\"}]\n\nTip: [1] is not a chat."));

        var chat = Assert.Single(chats);
        Assert.Equal("!abc:beeper.local", chat.Id);
        Assert.Equal("Savannah", chat.Title);
        Assert.False(chat.IsGroup);
    }

    [Fact]
    public void WordsInOneBlockAndJsonInAnotherAreBothLookedAt()
    {
        var result = new McpToolResult(
            false,
            [
                new McpContentBlock(McpContentKind.Text, "Results for your search (chatID is what send_message takes):", null, null, null),
                new McpContentBlock(McpContentKind.Text, """{"items":[{"id":"!abc:beeper.local","title":"Savannah","type":"single"}],"hasMore":false}""", null, null, null),
            ],
            null);

        Assert.Equal("Savannah", Assert.Single(ChatResults.Parse(result)).Title);
    }

    [Fact]
    public void AHeadingAndLabelledLinesForEachChatAreRead()
    {
        var chats = ChatResults.Parse(Text(
            "Found 2 chats:\n\n## Savannah\n- chatID: !abc:beeper.local\n- type: single\n- network: Beeper\n\n## Family\n- chatID: !def:beeper.local\n- type: group\n- network: WhatsApp\n\nUse chatID with send_message."));

        Assert.Equal(2, chats.Count);
        Assert.Equal("Savannah", chats[0].Title);
        Assert.Equal("!abc:beeper.local", chats[0].Id);
        Assert.Equal("Beeper", chats[0].Network);
        Assert.False(chats[0].IsGroup);
        Assert.Equal("Family", chats[1].Title);
        Assert.True(chats[1].IsGroup);
    }

    [Fact]
    public void AnExplicitTitleLineWinsOverTheHeading()
    {
        var chat = Assert.Single(ChatResults.Parse(Text("Chat\nTitle: Savannah\nchatID: !abc:beeper.local\nType: single")));

        Assert.Equal("Savannah", chat.Title);
        Assert.False(chat.IsGroup);
    }

    [Theory]
    [InlineData("1. **Savannah** (single, Beeper) chatID: !abc:beeper.local")]
    [InlineData("- Savannah - chatID: !abc:beeper.local (single chat on Beeper)")]
    [InlineData("Savannah [!abc:beeper.local] direct")]
    [InlineData("Savannah | !abc:beeper.local | single")]
    public void OneLineAChatIsRead(string line)
    {
        var chat = Assert.Single(ChatResults.Parse(Text("Found 1 chat:\n\n" + line + "\n\nUse chatID with send_message.")));

        Assert.Equal("!abc:beeper.local", chat.Id);
        Assert.Equal("Savannah", chat.Title);
        Assert.False(chat.IsGroup);
    }

    [Fact]
    public void SeveralChatsOnSeparateLinesAreEachRead()
    {
        var chats = ChatResults.Parse(Text("- Savannah (single) chatID: !a1:beeper.local\n- Lina (single) chatID: !b2:beeper.local\n- Family (group) chatID: !c3:beeper.local"));

        Assert.Equal(["Savannah", "Lina", "Family"], chats.Select(chat => chat.Title));
        Assert.Equal([false, false, true], chats.Select(chat => chat.IsGroup));
    }

    [Theory]
    [InlineData("I could not find that chat. Try another name.")]
    [InlineData("No chats matched. Use id: to look one up")]
    [InlineData("Savannah")]
    public void WordsWithNoIdListNoChat(string text)
    {
        Assert.Empty(ChatResults.Parse(Text(text)));
    }

    [Fact]
    public void AChatNamedByChatNameIsRead()
    {
        var chat = Assert.Single(ChatResults.Parse(Text("""{"chats":[{"chatID":"!x:y.z","chatName":"Savannah"}]}""")));

        Assert.Equal("Savannah", chat.Title);
    }

    // The way Beeper's own server answered a real search (names changed): Markdown for a model, with the chat's id in the heading.
    private const string BeeperAnswer =
        "# Chats\n\nFound 3 chats\n\n" +
        "## Omar (chatID: 131051)\nChat on Beeper (Matrix) (matrix) with Omar, me.\n**Type**: single\n**Last Activity**: 2026-10-04T18:25:13.749Z\n\n" +
        "## Omar (chatID: 130658)\nChat on Telegram (telegram) with Omar, me.\n**Type**: single\n**Last Activity**: 2026-05-01T16:56:37.000Z\nThis chat is archived.\n\n" +
        "## Family & Omar (chatID: 123807)\nChat on WhatsApp (local-whatsapp_ba_AbC) with Ali, Omar, Me.\n**Type**: group\n**Last Activity**: 2026-03-19T19:28:08.000Z\nThis chat is archived.\n\n" +
        "# Using this information\n\n- Pass the \"chatID\" to get_chat or search_messages for details about a chat, or send_message to send a message to a chat.\n" +
        "- Link the \"open\" link to the user to allow them to view the chat in Beeper Desktop.";

    [Fact]
    public void BeeperOwnMarkdownAnswerIsRead()
    {
        var chats = ChatResults.Parse(Text(BeeperAnswer));

        Assert.Equal(["131051", "130658", "123807"], chats.Select(chat => chat.Id));
        Assert.Equal(["Omar", "Omar", "Family & Omar"], chats.Select(chat => chat.Title));
        Assert.Equal(["Beeper (Matrix)", "Telegram", "WhatsApp"], chats.Select(chat => chat.Network));
        Assert.Equal([false, false, true], chats.Select(chat => chat.IsGroup));
        Assert.Equal([false, true, true], chats.Select(chat => chat.IsArchived));
    }

    [Fact]
    public void AnArchivedFlagInJsonIsRead()
    {
        var chats = ChatResults.Parse(Text("""{"items":[{"id":"a","title":"Omar","isArchived":true},{"id":"b","title":"Omar","isArchived":false}]}"""));

        Assert.Equal([true, false], chats.Select(chat => chat.IsArchived));
    }

    [Fact]
    public void AToolThatSaysItFailedListsNoChatEvenIfItsWordsLookLikeOne()
    {
        var result = new McpToolResult(true, [new McpContentBlock(McpContentKind.Text, """{"chats":[{"id":"c1"}]}""", null, null, null)], null);

        Assert.Empty(ChatResults.Parse(result));
    }

    [Fact]
    public void NoMoreThanFiftyChatsAreKept()
    {
        var many = string.Join(',', Enumerable.Range(0, 80).Select(index => $$"""{"id":"c{{index}}","title":"T{{index}}"}"""));

        Assert.Equal(50, ChatResults.Parse(Text($$"""{"chats":[{{many}}]}""")).Count);
    }

    [Fact]
    public void ATitleIsOneShortLineWithNoMarkup()
    {
        var chat = Assert.Single(ChatResults.Parse(Text("""{"chats":[{"id":"c1","title":"<b>Omar</b>\n\tthe  brother"}]}""")));

        Assert.Equal("b Omar /b the brother", chat.Title);
    }

    [Fact]
    public void AChatNeverPrintsItsWordsInToString()
    {
        var chat = Assert.Single(ChatResults.Parse(Text("""{"chats":[{"id":"SECRET-ID","title":"SECRET name","participants":[{"phone":"SECRET number"}]}]}""")));

        Assert.DoesNotContain("SECRET", chat.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"status":"pending"}""", true)]
    [InlineData("""{"status":"queued"}""", true)]
    [InlineData("""{"status":"sending"}""", true)]
    [InlineData("""{"status":"sent"}""", false)]
    [InlineData("Message sent.", false)]
    public void AResultThatSaysTheMessageIsStillOnItsWayIsPending(string text, bool expected)
    {
        Assert.Equal(expected, ChatResults.SaysPending(Text(text)));
    }

    [Fact]
    public void ThereIsNothingToReadInANullResult()
    {
        Assert.Throws<ArgumentNullException>(() => ChatResults.Parse(null!));
        Assert.Throws<ArgumentNullException>(() => ChatResults.SaysPending(null!));
        using var document = JsonDocument.Parse("{}");
        Assert.Empty(ChatResults.Parse(new McpToolResult(false, [], document.RootElement.Clone())));
    }
}
