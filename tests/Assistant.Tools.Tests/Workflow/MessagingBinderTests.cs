using Assistant.Tools.Mcp;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Tests.Mcp;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// Finding, among the tools of a connected app, the two the Assistant's messaging needs (PROJECT_SPEC §4.8, step 116): strict, by words and argument names only, and never a guess.
/// </summary>
public sealed class MessagingBinderTests
{
    private const string ChatAndText = """{"type":"object","properties":{"chat_id":{"type":"string"},"text":{"type":"string"}},"required":["chat_id","text"]}""";
    private const string Query = """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""";

    private static McpToolDescriptor Tool(string name, string schema, string description = "Does a thing.") => Sample.Tool(name, description, schema);

    [Fact]
    public void ASendToolThatTakesAChatIdIsBoundWithTheToolThatFindsChats()
    {
        var binding = MessagingBinder.Bind([Tool("search_chats", Query), Tool("send_message", ChatAndText)]);

        Assert.NotNull(binding);
        Assert.Equal("send_message", binding.Send.Tool.Name);
        Assert.Equal("chat_id", binding.Send.TargetArgument);
        Assert.Equal("text", binding.Send.TextArgument);
        Assert.Equal(SendTarget.ChatId, binding.Send.Target);
        Assert.Equal("search_chats", binding.Find!.Tool.Name);
        Assert.Equal("query", binding.Find.QueryArgument);
    }

    [Fact]
    public void ArgumentsAreFoundWhateverTheirCaseAndSpelling()
    {
        var binding = MessagingBinder.Bind(
        [
            Tool("searchChats", """{"type":"object","properties":{"searchTerm":{"type":"string"}},"required":["searchTerm"]}"""),
            Tool("sendMessage", """{"type":"object","properties":{"chatID":{"type":"string"},"messageText":{"type":"string"}},"required":["chatID","messageText"]}"""),
        ]);

        Assert.NotNull(binding);
        Assert.Equal("chatID", binding.Send.TargetArgument);
        Assert.Equal("messageText", binding.Send.TextArgument);
        Assert.Equal("searchTerm", binding.Find!.QueryArgument);
    }

    [Fact]
    public void AToolAddressedByANumberNeedsNoSearch()
    {
        var binding = MessagingBinder.Bind([Tool("send_message", """{"type":"object","properties":{"to":{"type":"string"},"body":{"type":"string"}},"required":["to","body"]}""")]);

        Assert.NotNull(binding);
        Assert.Equal(SendTarget.Address, binding.Send.Target);
        Assert.Equal("to", binding.Send.TargetArgument);
        Assert.Equal("body", binding.Send.TextArgument);
        Assert.Null(binding.Find);
    }

    [Fact]
    public void AToolThatTakesAChatIdAndHasNoWayToFindChatsIsNotBound()
    {
        Assert.Null(MessagingBinder.Bind([Tool("send_message", ChatAndText)]));
    }

    [Fact]
    public void AToolThatMustBeGivenSomethingTheAssistantCannotSayIsNotBound()
    {
        var needsAnAccount = """{"type":"object","properties":{"chat_id":{"type":"string"},"text":{"type":"string"},"account":{"type":"string"}},"required":["chat_id","text","account"]}""";

        Assert.Null(MessagingBinder.Bind([Tool("search_chats", Query), Tool("send_message", needsAnAccount)]));
    }

    [Fact]
    public void AnOptionalExtraArgumentIsFine()
    {
        var withOptional = """{"type":"object","properties":{"chat_id":{"type":"string"},"text":{"type":"string"},"silent":{"type":"boolean"}},"required":["chat_id","text"]}""";

        Assert.NotNull(MessagingBinder.Bind([Tool("search_chats", Query), Tool("send_message", withOptional)]));
    }

    [Theory]
    [InlineData("draft_message")]
    [InlineData("schedule_message")]
    [InlineData("delete_message")]
    [InlineData("edit_message")]
    [InlineData("list_messages")]
    [InlineData("search_messages")]
    [InlineData("react_to_message")]
    [InlineData("forward_message")]
    public void AToolThatDraftsSchedulesEditsDeletesOrReadsIsNeverTheOneThatSends(string name)
    {
        Assert.Null(MessagingBinder.Bind([Tool("search_chats", Query), Tool(name, ChatAndText)]));
    }

    [Fact]
    public void AToolNamedForSendingComesBeforeOneThatOnlyPosts()
    {
        var binding = MessagingBinder.Bind([Tool("search_chats", Query), Tool("post_message", ChatAndText), Tool("send_message", ChatAndText)]);

        Assert.Equal("send_message", binding!.Send.Tool.Name);
    }

    [Theory]
    [InlineData("search_messages")]
    [InlineData("get_chat")]
    [InlineData("create_chat")]
    [InlineData("send_chat_message")]
    [InlineData("mute_chat")]
    public void AToolThatIsNotASearchForChatsIsNotTheOneThatFindsThem(string name)
    {
        var schema = name == "get_chat" ? """{"type":"object","properties":{"chat_id":{"type":"string"}},"required":["chat_id"]}""" : Query;

        Assert.Null(MessagingBinder.Bind([Tool(name, schema), Tool("send_message", ChatAndText)]));
    }

    [Theory]
    [InlineData("search_contacts")]
    [InlineData("find_chat")]
    [InlineData("list_chats")]
    [InlineData("lookup_conversation")]
    public void AToolThatLooksChatsOrContactsUpIsTheFinder(string name)
    {
        var binding = MessagingBinder.Bind([Tool(name, Query), Tool("send_message", ChatAndText)]);

        Assert.NotNull(binding);
        Assert.Equal(name, binding.Find!.Tool.Name);
    }

    [Fact]
    public void AnArgumentThatIsNotATextIsNotUsedForTheTextOrTheTarget()
    {
        var recipientsAreAList = """{"type":"object","properties":{"recipients":{"type":"array","items":{"type":"string"}},"text":{"type":"string"}},"required":["recipients","text"]}""";

        Assert.Null(MessagingBinder.Bind([Tool("send_message", recipientsAreAList)]));
    }

    [Theory]
    [InlineData("send_message", true)]
    [InlineData("sendMessage", true)]
    [InlineData("post_message", true)]
    [InlineData("whatsapp_send", false)]
    [InlineData("list_notes", false)]
    [InlineData("draft_message", false)]
    [InlineData("create_task", false)]
    public void TheNamesAnAppKeptSayWhetherItCouldSendAText(string name, bool expected)
    {
        Assert.Equal(expected, MessagingBinder.NamesSuggestSending([name]));
    }

    [Fact]
    public void NoNamesAreNoSuggestion()
    {
        Assert.False(MessagingBinder.NamesSuggestSending([]));
    }
    [Theory]
    [InlineData("""{"type":"object","properties":{"chatID":{"type":"string"},"draftText":{"type":"string"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"chat_id":{"type":"string"},"draft_text":{"type":"string"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"chat_id":{"type":"string"},"draft":{"type":"string"}}}""", true)]
    [InlineData("""{"type":"object","properties":{"chat_id":{"type":"string"},"text":{"type":"string"}}}""", false)]
    [InlineData("""{"type":"object","properties":{}}""", false)]
    public void AToolThatTakesWordsForADraftIsToldByItsArguments(string schema, bool writes) =>
        Assert.Equal(writes, MessagingBinder.WritesDraft(Tool("focus_app", schema)));
}