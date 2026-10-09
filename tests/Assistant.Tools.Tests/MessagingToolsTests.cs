using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Core.Tools;
using Assistant.Tools.Messaging;
using Xunit;

namespace Assistant.Tools.Tests;

/// <summary>
/// The messaging tools and the sample provider they send through (PROJECT_SPEC §4.8, step 113): a message goes only to a saved person, is drafted before it is sent, is sent only for
/// a call the user confirmed, and a request that cannot be settled (two brothers, no brother, no way to reach one) sends nothing and tells the model to ask.
/// </summary>
public sealed class MessagingToolsTests
{
    private const string Phone = "+44 7700 900123";
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryPersonStore _store = new();
    private readonly MockMessagingProvider _provider = new();

    private DraftMessageTool Draft => new(_provider, new PersonResolver(_store));

    private SendMessageTool Send => new(_provider, new PersonResolver(_store));

    private async Task<Person> Save(string name, string[]? relationships = null, string[]? aliases = null, bool reachable = true) =>
        await _store.SaveAsync(Person.Create(name, Now) with
        {
            Relationships = relationships ?? [],
            Aliases = aliases ?? [],
            Identifiers = reachable ? [new(PersonIdentifierKind.Phone, Phone, "WhatsApp"), new(PersonIdentifierKind.Email, "omar@example.com")] : [],
        });

    private static async Task<ToolResult> RunAsync(ITool tool, string arguments, ToolContext? context = null) =>
        await tool.RunAsync(
            new ToolCall("call-1", tool.Definition.Name, arguments),
            JsonDocument.Parse(arguments).RootElement.Clone(),
            context ?? new ToolContext(Guid.NewGuid(), "text my brother"),
            CancellationToken.None);

    private static JsonElement Json(ToolResult result) => JsonDocument.Parse(result.OutputJson).RootElement.Clone();

    private static string Args(string recipient, string text) => JsonSerializer.Serialize(new { recipient, text });

    // ---- What they are --------------------------------------------------------------------------------------------------

    [Fact]
    public void DraftingOnlyReadsAndSendingChangesStateAndBothNeedTheMessagingPermission()
    {
        var draft = Draft.Definition;
        var send = Send.Definition;

        Assert.Equal("draft_message", draft.Name);
        Assert.Equal("send_message", send.Name);
        Assert.Equal(RiskLevel.ReadOnly, draft.RiskLevel);
        Assert.Equal(RiskLevel.SideEffect, send.RiskLevel);
        Assert.Equal(PermissionCapability.Messaging, draft.RequiredPermission);
        Assert.Equal(PermissionCapability.Messaging, send.RequiredPermission);
        Assert.Null(ToolDefinitionGuard.Problem(draft, draft.EffectiveTimeout));
        Assert.Null(ToolDefinitionGuard.Problem(send, send.EffectiveTimeout));
        Assert.True(MessagingToolResults.IsMessagingTool(draft.Name) && MessagingToolResults.IsMessagingTool(send.Name));
        Assert.False(MessagingToolResults.IsMessagingTool("calculate"));
    }

    [Fact]
    public void BothTakeARecipientATextAndWhichChatTheUserChose_AndNothingElse()
    {
        foreach (var tool in new ITool[] { Draft, Send })
        {
            using var schema = JsonDocument.Parse(tool.Definition.InputSchemaJson);
            var properties = schema.RootElement.GetProperty("properties");

            Assert.Equal(["recipient", "text"], schema.RootElement.GetProperty("required").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(["recipient", "text", "via"], properties.EnumerateObject().Select(property => property.Name));
            Assert.Equal(MessagingToolResults.MaxRecipientLength, properties.GetProperty("via").GetProperty("maxLength").GetInt32());
            Assert.Equal(MessagingToolResults.MaxRecipientLength, properties.GetProperty("recipient").GetProperty("maxLength").GetInt32());
            Assert.Equal(MessagingToolResults.MaxTextLength, properties.GetProperty("text").GetProperty("maxLength").GetInt32());
            Assert.False(schema.RootElement.GetProperty("additionalProperties").GetBoolean());
        }
    }

    // ---- draft_message --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheToolChecksTheMessagingPermissionItselfAndLooksNothingUpWhileItIsOffOrSetToAskAndNotAskedAbout()
    {
        await Save("Omar Hassan", ["Brother"]);
        foreach (var permissions in new[]
                 {
                     new Assistant.Core.Settings.PermissionSettings { Messaging = false },
                     Assistant.Core.Permissions.PermissionSettingsExtensions.WithMode(
                         new Assistant.Core.Settings.PermissionSettings(), PermissionCapability.Messaging, PermissionMode.AskEveryTime),
                 })
        {
            var provider = new MockMessagingProvider();
            var policy = new Assistant.Core.Permissions.SettingsPermissionPolicy(
                new FixedSettings { Current = new Assistant.Core.Settings.AppSettings { Permissions = permissions } });

            var result = await RunAsync(new DraftMessageTool(provider, new PersonResolver(_store), policy), Args("my brother", "On my way."));

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Contains("Messaging", result.OutputJson, StringComparison.Ordinal);
            Assert.Empty(provider.Sent);
        }
    }

    [Fact]
    public async Task ASetToAskPermissionStillGetsTheToolsOfferedAndAnApprovedUseDrafts()
    {
        await Save("Omar Hassan", ["Brother"]);
        var asking = Assistant.Core.Permissions.PermissionSettingsExtensions.WithMode(
            new Assistant.Core.Settings.PermissionSettings(), PermissionCapability.Messaging, PermissionMode.AskEveryTime);
        var policy = new Assistant.Core.Permissions.SettingsPermissionPolicy(
            new FixedSettings { Current = new Assistant.Core.Settings.AppSettings { Permissions = asking } });
        var tool = new DraftMessageTool(_provider, new PersonResolver(_store), policy);
        var context = new ToolContext(Guid.NewGuid(), "text my brother");

        await tool.PrepareAsync(context, CancellationToken.None);
        var offered = tool.IsOffered(context);
        ToolResult drafted;
        using (Assistant.Core.Permissions.PermissionApprovals.Approve(PermissionCapability.Messaging))
        {
            drafted = await RunAsync(tool, Args("my brother", "On my way."), context);
        }

        Assert.True(offered);
        Assert.Equal(ToolResultStatus.Succeeded, drafted.Status);
        Assert.Equal("drafted", Json(drafted).GetProperty("status").GetString());
    }

    [Fact]
    public async Task MyBrotherIsDraftedForTheSavedBrother_AndNothingIsSent()
    {
        await Save("Omar Hassan", ["Brother"]);
        await Save("Sara Ahmed", ["Sister"]);

        var result = await RunAsync(Draft, Args("my brother", "On my way, 10 minutes."));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        var json = Json(result);
        Assert.Equal("messaging", json.GetProperty("source").GetString());
        Assert.Equal("drafted", json.GetProperty("status").GetString());
        Assert.False(json.GetProperty("sent").GetBoolean());
        Assert.Equal("Omar Hassan", json.GetProperty("to").GetString());
        Assert.Equal("WhatsApp chat with Omar Hassan", json.GetProperty("via").GetString());
        Assert.Equal("On my way, 10 minutes.", json.GetProperty("text").GetString());
        Assert.Equal("Sample Messages", json.GetProperty("provider").GetString());
        Assert.True(json.GetProperty("sample").GetBoolean());
        Assert.Contains("Nothing has been sent", json.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Contains("only a sample", json.GetProperty("note").GetString(), StringComparison.Ordinal);
        Assert.Empty(_provider.Sent);
        Assert.Equal("Omar Hassan", Assert.Single(_provider.Drafted).Recipient.DisplayName);
    }

    [Fact]
    public async Task ARecipientIsFoundByNameAliasOrRelationship()
    {
        await Save("Omar Hassan", ["Brother"], ["Omi"]);

        foreach (var reference in new[] { "Omar", "Omi", "my bro", "my brother Omar" })
        {
            Assert.Equal("Omar Hassan", Json(await RunAsync(Draft, Args(reference, "hi"))).GetProperty("to").GetString());
        }
    }

    [Fact]
    public async Task NoResultEverHoldsANumberAnAddressOrAUsername()
    {
        await Save("Omar Hassan", ["Brother"]);

        var drafted = (await RunAsync(Draft, Args("my brother", "hi"))).OutputJson;
        var sent = (await RunAsync(Send, Args("Omar", "hi"))).OutputJson;

        foreach (var output in new[] { drafted, sent })
        {
            Assert.DoesNotContain("7700", output, StringComparison.Ordinal);
            Assert.DoesNotContain("example.com", output, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task TheTextIsTidiedAndKeptWordForWord()
    {
        await Save("Omar", ["Brother"]);

        var result = await RunAsync(Draft, Args("Omar", "  Line one\r\nLine \"two\" <3 + more\u0007\u0000 "));

        Assert.Equal("Line one\nLine \"two\" <3 + more", Json(result).GetProperty("text").GetString());
        Assert.Contains("Line \\\"two\\\" <3 + more", result.OutputJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMessageWithNothingInItIsRefusedWithHowToCallTheTool()
    {
        await Save("Omar", ["Brother"]);

        var result = await RunAsync(Draft, Args("Omar", " \r\n\u0007 "));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(ToolErrors.InvalidArguments, Json(result).GetProperty("code").GetString());
        Assert.Empty(_provider.Drafted);
    }

    [Theory]
    [InlineData("\"hello\"", "hello")]
    [InlineData("\"\\\"hello\\\"\"", "hello")] // A string inside a string, as a model once wrote it.
    [InlineData("\\\"hello\\\"", "hello")]
    [InlineData("“hello there”", "hello there")]
    [InlineData("'on my way'", "on my way")]
    [InlineData("  \"hello\"  ", "hello")]
    [InlineData("She said \"hello\" to me", "She said \"hello\" to me")] // Marks that are part of what is said stay.
    [InlineData("\"hi\" and \"bye\"", "\"hi\" and \"bye\"")]
    [InlineData("I'm late, don't wait", "I'm late, don't wait")]
    [InlineData("hello", "hello")]
    public void QuotationMarksThatOnlyWrapTheMessageAreNotPartOfIt(string given, string sent) =>
        Assert.Equal(sent, MessagingToolResults.CleanText(given));

    [Fact]
    public async Task AMessageHandedOverInQuotationMarksIsDraftedAsItsWords()
    {
        await Save("Omar", ["Brother"]);

        var result = await RunAsync(Draft, Args("Omar", "\"\\\"hello\\\"\""));

        Assert.Equal("hello", Json(result).GetProperty("text").GetString());
        Assert.Equal("hello", Assert.Single(_provider.Drafted).Text);
    }

    // The user who changes the words in the question's field before they press Send.
    private sealed class EditingUser(string? edited, bool approve = true) : IPermissionService
    {
        public ToolConfirmation? Shown { get; private set; }

        public Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            Shown = confirmation;
            if (edited is not null)
            {
                confirmation.Edit!.Set(edited);
            }

            return Task.FromResult(approve ? ConfirmationDecision.Approved : ConfirmationDecision.Declined);
        }
    }

    [Theory]
    [InlineData("see you at 7 instead", "see you at 7 instead")]
    [InlineData("  \"see you at 7\"  ", "see you at 7")] // What the user typed is tidied as the model's words are.
    [InlineData("", "see you at 6")] // An emptied field sends what was drafted: the question does not let it be sent empty.
    [InlineData(null, "see you at 6")] // Nothing changed.
    public async Task TheWordsTheUserLeftInTheQuestionAreTheWordsThatAreSent_ToTheSameChat(string? edited, string sent)
    {
        await Save("Omar Hassan", ["Brother"]);
        var user = new EditingUser(edited);
        var executor = new ToolExecutor([Draft, Send], permissions: user, policy: new FakePermissions(true));

        var result = await executor.ExecuteAsync(Call("send_message", Args("Omar", "see you at 6")), new ToolContext(Guid.NewGuid(), "text omar"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);

        // The question says its Message line can be rewritten, and that the other button cancels.
        Assert.Equal("Message", user.Shown!.Edit!.Label);
        Assert.Equal(MessagingToolResults.MaxTextLength, user.Shown.Edit.MaxLength);
        Assert.Equal("Cancel", user.Shown.DeclineLabel);

        // One draft was made, and it is that chat the edited words went to.
        Assert.Single(_provider.Drafted);
        var message = Assert.Single(_provider.Sent);
        Assert.Equal(sent, message.Message.Text);
        Assert.Equal("WhatsApp chat with Omar Hassan", message.Route);
        Assert.Equal(sent, Json(result).GetProperty("text").GetString());
    }

    [Fact]
    public async Task WordsTheUserChangedAndThenCancelledAreNotSent()
    {
        await Save("Omar Hassan", ["Brother"]);
        var executor = new ToolExecutor([Draft, Send], permissions: new EditingUser("something else", approve: false), policy: new FakePermissions(true));

        var result = await executor.ExecuteAsync(Call("send_message", Args("Omar", "see you at 6")), new ToolContext(Guid.NewGuid(), "text omar"));

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public void WhatTheUserTypedIntoAQuestionIsNeverInItsText()
    {
        var edit = new ConfirmationEdit("Message", 10);
        edit.Set("a private note that is too long");

        Assert.Equal("a private ", edit.Value);
        Assert.DoesNotContain("private", edit.ToString(), StringComparison.Ordinal);
    }

    // ---- who is meant ---------------------------------------------------------------------------------------------------
    [Fact]
    public async Task TwoBrothersAreAQuestionForTheUser_AndNothingIsDraftedOrSent()
    {
        await Save("Omar Hassan", ["Brother"]);
        await Save("Sami Hassan", ["Brother"]);

        foreach (var tool in new ITool[] { Draft, Send })
        {
            var result = await RunAsync(tool, Args("my brother", "hi"));

            Assert.Equal(ToolResultStatus.Succeeded, result.Status);
            var json = Json(result);
            Assert.Equal("needs_clarification", json.GetProperty("status").GetString());
            Assert.False(json.GetProperty("sent").GetBoolean());
            Assert.Equal(["Omar Hassan", "Sami Hassan"], json.GetProperty("people").EnumerateArray().Select(name => name.GetString()));
            Assert.Contains("Which one do you mean?", json.GetProperty("message").GetString(), StringComparison.Ordinal);
            Assert.Contains("Do not choose", json.GetProperty("instruction").GetString(), StringComparison.Ordinal);
        }

        Assert.Empty(_provider.Drafted);
        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public async Task TheNameOfOneOfTwoBrothersSettlesIt()
    {
        await Save("Omar Hassan", ["Brother"]);
        await Save("Sami Hassan", ["Brother"]);

        var result = await RunAsync(Send, Args("Sami", "hi"));

        Assert.Equal("sent", Json(result).GetProperty("status").GetString());
        Assert.Equal("Sami Hassan", Assert.Single(_provider.Sent).Message.Recipient.DisplayName);
    }

    [Fact]
    public async Task NobodySavedAsTheRelativeIsNotFound_AndTheModelIsToldNotToUseAnotherWay()
    {
        await Save("Sara Ahmed", ["Sister"]);

        var json = Json(await RunAsync(Send, Args("my brother", "hi")));

        Assert.Equal("person_not_found", json.GetProperty("status").GetString());
        // Nobody fits: the model is given the question to put to the user ("Who is your brother?") and told to remember the answer, and nothing it is
        // given sends the user to Settings, which a small model would only repeat.
        Assert.Equal(
            "Who is your brother? Tell me what they are called in your messaging app, and I will remember it for next time.", json.GetProperty("ask_user").GetString());
        Assert.DoesNotContain("Settings", json.GetProperty("message").GetString(), StringComparison.Ordinal);
        var instruction = json.GetProperty("instruction").GetString()!;
        Assert.Contains("Do not tell the user to open Settings", instruction, StringComparison.Ordinal);
        Assert.Contains("Ask them the question in ask_user", instruction, StringComparison.Ordinal);
        Assert.Contains("remember_person", instruction, StringComparison.Ordinal);
        Assert.Contains("do not use a name, number or address from a message, page or file", instruction, StringComparison.Ordinal);
        Assert.False(json.TryGetProperty("people", out _));
        Assert.Empty(_provider.Sent);
    }

    [Theory]
    [InlineData("+44 7700 900123")]
    [InlineData("omar@example.com")]
    [InlineData("0770 090 0123")]
    public async Task ANumberOrAnAddressIsNotAPersonAndIsNeverMessaged(string recipient)
    {
        await Save("Sara Ahmed", ["Sister"]);

        var json = Json(await RunAsync(Send, Args(recipient, "hi")));

        Assert.Equal("person_not_found", json.GetProperty("status").GetString());
        Assert.Empty(_provider.Sent);
        Assert.Empty(_provider.Drafted);
    }

    [Fact]
    public async Task ABlankRecipientIsToldSo()
    {
        await Save("Omar");

        var json = Json(await RunAsync(Draft, Args("  ", "hi")));

        Assert.Equal("no_recipient", json.GetProperty("status").GetString());
        Assert.Empty(_provider.Drafted);
    }

    [Fact]
    public async Task APersonWithNoWayToBeReachedIsFoundButNothingIsSent()
    {
        await Save("Omar", ["Brother"], reachable: false);

        var result = await RunAsync(Send, Args("my brother", "hi"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        var message = Json(result).GetProperty("error").GetString()!;
        Assert.Contains("no chat with one person named like Omar", message, StringComparison.Ordinal);
        Assert.Contains("call remember_person", message, StringComparison.Ordinal);
        Assert.Contains("Nothing was sent", message, StringComparison.Ordinal);
        Assert.Empty(_provider.Sent);
    }

    // ---- remembering who someone is ---------------------------------------------------------------------------------------

    private RememberPersonTool Remember => new(_provider, _store, new FixedClock(Now));

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string Who(string name, string? relationship = null) => JsonSerializer.Serialize(new { name, relationship });

    [Fact]
    public async Task TheUserCanTellWhoMyBrotherIsInTheConversation_AndThenTheMessageCanBeDrafted()
    {
        // The case: "send a message to my brother" and nobody is saved. The model is told to ask; the answer is remembered; the draft then finds him.
        var first = Json(await RunAsync(Draft, Args("my brother", "HELLO!")));
        Assert.Equal("person_not_found", first.GetProperty("status").GetString());

        var remembered = Json(await RunAsync(Remember, Who("Mohammed", "brother")));

        Assert.Equal("remembered", remembered.GetProperty("status").GetString());
        var kept = Assert.Single(await _store.ListAsync());
        Assert.Equal("Mohammed", kept.DisplayName);
        Assert.Equal(["brother"], kept.Relationships.Select(item => item.ToLowerInvariant()).ToArray());
        var chat = Assert.Single(kept.Identifiers);
        Assert.Equal(PersonIdentifierKind.ChatName, chat.Kind);
        Assert.Equal("Mohammed", chat.Value);

        // Only a name and a relationship were kept: no number, no address.
        Assert.DoesNotContain(kept.Identifiers, identifier => identifier.Kind != PersonIdentifierKind.ChatName);
        Assert.DoesNotContain("Mohammed@", remembered.GetRawText(), StringComparison.Ordinal);

        var drafted = Json(await RunAsync(Draft, Args("my brother", "HELLO!")));
        Assert.Equal("drafted", drafted.GetProperty("status").GetString());
        Assert.Equal("Mohammed", drafted.GetProperty("to").GetString());
    }

    [Fact]
    public async Task RememberingSomeoneAlreadySavedAddsWhatIsNewAndNeverMakesTwoOfThem()
    {
        var omar = await Save("Omar", ["Colleague"]);

        await RunAsync(Remember, Who("omar", "brother"));
        await RunAsync(Remember, Who("Omar", "Brother"));

        var kept = Assert.Single(await _store.ListAsync());
        Assert.Equal(omar.Id, kept.Id);
        Assert.Equal(2, kept.Relationships.Count);
        Assert.Contains(kept.Identifiers, identifier => identifier.Kind == PersonIdentifierKind.Phone);
        Assert.Single(kept.Identifiers, identifier => identifier.Kind == PersonIdentifierKind.ChatName);
    }

    [Fact]
    public async Task RememberingAskedForTheUsersApprovalShowsExactlyWhatWouldBeKept_AndKeepsNothingBeforeAYes()
    {
        var tool = Remember;
        var arguments = Who("Mohammed", "brother");

        var plan = await tool.PlanAsync(new ToolCall("c", tool.Definition.Name, arguments), JsonDocument.Parse(arguments).RootElement.Clone(), new ToolContext(Guid.NewGuid(), "x"), CancellationToken.None);

        Assert.Equal(RiskLevel.SideEffect, tool.Definition.RiskLevel);
        Assert.Contains("Mohammed", plan.Confirmation!.Title, StringComparison.Ordinal);
        Assert.Contains(plan.Confirmation.Details, detail => detail.Value == "Mohammed");
        Assert.Empty(await _store.ListAsync());

        var result = await plan.RunAsync(CancellationToken.None);

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        Assert.Single(await _store.ListAsync());
    }

    [Fact]
    public async Task AHandleGivenForAPersonIsHowTheyAreReached_AndNotWhatTheyAreCalled()
    {
        // "@marcus:beeper.com" given as the name: the person is called by the name in it, and the handle is kept as how they are reached.
        var remembered = Json(await RunAsync(Remember, Who("@marcus:beeper.com", "brother")));

        Assert.Equal("remembered", remembered.GetProperty("status").GetString());
        var kept = Assert.Single(await _store.ListAsync());
        Assert.Equal("Marcus", kept.DisplayName);
        var handle = Assert.Single(kept.Identifiers, identifier => identifier.Kind == PersonIdentifierKind.Username);
        Assert.Equal(("@marcus:beeper.com", "Beeper"), (handle.Value, handle.Service));
        Assert.Contains(kept.Identifiers, identifier => identifier is { Kind: PersonIdentifierKind.ChatName, Value: "Marcus" });
        Assert.Equal(["brother"], kept.Relationships.Select(item => item.ToLowerInvariant()).ToArray());
    }

    [Fact]
    public async Task AHandleForSomeoneAlreadySavedIsAddedToThem_AndNoSecondPersonIsMade()
    {
        var marcus = await _store.SaveAsync(Person.Create("Marcus", Now) with { Relationships = ["Brother"], Identifiers = [new(PersonIdentifierKind.ChatName, "Marcus")] });
        var tool = Remember;
        var arguments = Who("@marcus:beeper.com", "brother");

        // The user is shown the handle as how the person is reached, under the person's own name.
        var plan = await tool.PlanAsync(new ToolCall("c", tool.Definition.Name, arguments), JsonDocument.Parse(arguments).RootElement.Clone(), new ToolContext(Guid.NewGuid(), "x"), CancellationToken.None);
        Assert.Contains(plan.Confirmation!.Details, detail => detail is { Label: "Name", Value: "Marcus" });
        Assert.Contains(plan.Confirmation.Details, detail => detail.Label == "Reached by" && detail.Value == "@marcus:beeper.com (Beeper)");

        await plan.RunAsync(CancellationToken.None);

        var kept = Assert.Single(await _store.ListAsync());
        Assert.Equal((marcus.Id, "Marcus"), (kept.Id, kept.DisplayName));
        Assert.Contains(kept.Identifiers, identifier => identifier is { Kind: PersonIdentifierKind.Username, Value: "@marcus:beeper.com" });
    }

    [Fact]
    public async Task SomeoneSavedUnderAHandleAndAgainUnderTheirNameIsOnePersonToMessage()
    {
        // What 0.1.141 could leave behind: the handle as one person and the name as another, both "my brother". They are one person, and nobody is asked which.
        await _store.SaveAsync(Person.Create("@marcus:beeper.com", Now) with { Relationships = ["Brother"], Identifiers = [new(PersonIdentifierKind.ChatName, "@marcus:beeper.com")] });
        await _store.SaveAsync(Person.Create("marcus", Now) with { Relationships = ["Brother"], Identifiers = [new(PersonIdentifierKind.ChatName, "marcus")] });

        var resolution = await new PersonResolver(_store).ResolveAsync("my brother");

        Assert.Equal(PersonResolutionOutcome.Found, resolution.Outcome);
        var person = Assert.Single(resolution.Candidates);
        Assert.Equal("marcus", person.DisplayName);
        Assert.Contains(person.Identifiers, identifier => identifier is { Kind: PersonIdentifierKind.Username, Value: "@marcus:beeper.com", Service: "Beeper" });

        // Nothing was changed in what is saved: the user tidies People themselves.
        Assert.Equal(2, (await _store.ListAsync()).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ANameThatIsNotThereIsRefusedAndNothingIsKept(string name)
    {
        var result = await RunAsync(Remember, Who(name, "brother"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Empty(await _store.ListAsync());
    }

    [Fact]
    public void RememberingIsOfferedOnlyWithAMessagingAppAndOnlyForMessaging()
    {
        var context = new ToolContext(Guid.NewGuid(), "Send a message to my brother");
        var tool = Remember;

        Assert.False(new RememberPersonTool(null, _store, new FixedClock(Now)).IsOffered(context));
        Assert.False(new RememberPersonTool(_provider, null, new FixedClock(Now)).IsOffered(context));
        Assert.True(tool.IsOffered(context));
        Assert.False(tool.IsOffered(new ToolContext(Guid.NewGuid(), "What is 2+2?")));
        Assert.True(MessagingToolResults.IsMessagingTool(tool.Definition.Name));

        // The Assistant's own words to the model keep its three tools to saved names: nothing in the arguments can carry a number or an address.
        foreach (var forbidden in new[] { "phone", "number", "address", "email" })
        {
            Assert.DoesNotContain($"\"{forbidden}\"", tool.Definition.InputSchemaJson, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- sending --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AMessageIsSentToTheResolvedPersonWithExactlyTheTextGiven()
    {
        var omar = await Save("Omar Hassan", ["Brother"]);

        var result = await RunAsync(Send, Args("Omar Hassan", "Running late, start without me"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);
        var json = Json(result);
        Assert.Equal("sent", json.GetProperty("status").GetString());
        Assert.True(json.GetProperty("sent").GetBoolean());
        Assert.Equal("Omar Hassan", json.GetProperty("to").GetString());
        Assert.Contains("only a sample", json.GetProperty("note").GetString(), StringComparison.Ordinal);
        var sent = Assert.Single(_provider.Sent);
        Assert.Equal("Running late, start without me", sent.Message.Text);
        Assert.Equal(omar.Id, sent.Message.Recipient.PersonId);
        Assert.Equal("WhatsApp chat with Omar Hassan", sent.Route);
    }

    [Fact]
    public async Task AMessageThatIsStillBeingSentIsNotCalledDelivered()
    {
        await Save("Omar");
        _provider.ReportSendsAs(MessageDeliveryStatus.Pending);

        var json = Json(await RunAsync(Send, Args("Omar", "hi")));

        Assert.Equal("pending", json.GetProperty("status").GetString());
        Assert.Contains("do not say it was delivered", json.GetProperty("note").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(MessagingFailure.Unavailable, "could not be reached")]
    [InlineData(MessagingFailure.SignInNeeded, "needs the user to sign in")]
    [InlineData(MessagingFailure.RecipientNotFound, "no chat with one person named like Omar")]
    [InlineData(MessagingFailure.Rejected, "did not accept the message")]
    public async Task AFailureOfTheProviderIsToldInWordsAndNothingIsSent(MessagingFailure failure, string words)
    {
        await Save("Omar");
        _provider.FailWith(failure);

        foreach (var tool in new ITool[] { Draft, Send })
        {
            var result = await RunAsync(tool, Args("Omar", "hi"));

            Assert.Equal(ToolResultStatus.Failed, result.Status);
            var error = Json(result).GetProperty("error").GetString()!;
            Assert.Contains(words, error, StringComparison.Ordinal);
            Assert.Contains("Sample Messages", error, StringComparison.Ordinal);
            Assert.Contains("Nothing was sent", error, StringComparison.Ordinal);
        }

        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public async Task SeveralChatsAreAQuestionToo_WithTheServicesTheyAreOn()
    {
        await Save("Omar");
        _provider.FailWith(MessagingFailure.Ambiguous, "WhatsApp", "Signal");

        var error = Json(await RunAsync(Send, Args("Omar", "hi"))).GetProperty("error").GetString()!;

        Assert.Contains("more than one chat with Omar (WhatsApp, Signal)", error, StringComparison.Ordinal);
        Assert.Contains("Ask the user which one to use", error, StringComparison.Ordinal);
        Assert.Contains("with via set to their answer, such as WhatsApp", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAProviderOrAListOfPeopleOrWhenThePeopleCannotBeReadNothingIsSent()
    {
        await Save("Omar");

        var noProvider = await RunAsync(new SendMessageTool(null, new PersonResolver(_store)), Args("Omar", "hi"));
        var noPeople = await RunAsync(new SendMessageTool(_provider, null), Args("Omar", "hi"));
        var broken = await RunAsync(new SendMessageTool(_provider, new PersonResolver(new BrokenStore())), Args("Omar", "hi"));

        foreach (var result in new[] { noProvider, noPeople, broken })
        {
            Assert.Equal(ToolResultStatus.Failed, result.Status);
            Assert.Equal(ToolErrors.Failed, Json(result).GetProperty("code").GetString());
        }

        Assert.Contains("No messaging app", Json(noProvider).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Contains("could not be read", Json(broken).GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.Empty(_provider.Sent);
    }

    // ---- when they are offered ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Text my brother that I'm late", true)]
    [InlineData("Send a WhatsApp to Omar", true)]
    [InlineData("Let my mom know I'm on my way", true)]
    [InlineData("Tell Sara thanks", true)]
    [InlineData("What is 15% of 80?", false)]
    [InlineData("Summarize this document", false)]
    [InlineData("Open the calculator", false)]
    public void TheyAreOfferedForARequestThatSeemsToBeAboutMessagingAndOnlyWithAProvider(string request, bool offered)
    {
        var context = new ToolContext(Guid.NewGuid(), request);

        Assert.Equal(offered, Draft.IsOffered(context));
        Assert.Equal(offered, Send.IsOffered(context));
        Assert.False(new DraftMessageTool(null, new PersonResolver(_store)).IsOffered(context));
        Assert.False(new SendMessageTool(null, new PersonResolver(_store)).IsOffered(context));
    }

    [Fact]
    public void ACallOutsideATurnCannotBeTold_SoTheToolsAreOffered()
    {
        Assert.True(Draft.IsOffered(new ToolContext(Guid.NewGuid(), null)));
    }

    [Fact]
    public void TheRegistryOffersThemOnlyForMessagingRequests_AndNeverWithoutAProvider()
    {
        var with = new ToolRegistry([Draft, Send]);
        var without = new ToolRegistry([new DraftMessageTool(null, null), new SendMessageTool(null, null)]);

        Assert.Equal(["draft_message", "send_message"], with.ToolsFor(new ToolContext(Guid.NewGuid(), "message my sister")).Select(tool => tool.Name));
        Assert.Empty(with.ToolsFor(new ToolContext(Guid.NewGuid(), "what is the capital of France")));
        Assert.Empty(without.ToolsFor(new ToolContext(Guid.NewGuid(), "message my sister")));
        Assert.Equal(2, without.Tools.Count);
    }

    // ---- through the executor (the permission and the confirmation) ----------------------------------------------------

    private ToolExecutor Executor(bool permission, FakeConfirmation? confirmation = null) =>
        new([Draft, Send], permissions: confirmation, policy: new FakePermissions(permission));

    private static ToolCall Call(string tool, string arguments) => new("call-1", tool, arguments);

    [Fact]
    public async Task SendingRunsOnlyWhenTheUserConfirmsTheCall()
    {
        await Save("Omar");
        var declined = new FakeConfirmation(approve: false);
        var approved = new FakeConfirmation(approve: true);

        var no = await Executor(true, declined).ExecuteAsync(Call("send_message", Args("Omar", "hi")), new ToolContext(Guid.NewGuid(), "text omar"));
        Assert.Equal(ToolResultStatus.Declined, no.Status);
        Assert.Empty(_provider.Sent);
        Assert.Equal(1, declined.Asked);

        var yes = await Executor(true, approved).ExecuteAsync(Call("send_message", Args("Omar", "hi")), new ToolContext(Guid.NewGuid(), "text omar"));
        Assert.Equal(ToolResultStatus.Succeeded, yes.Status);
        Assert.Single(_provider.Sent);
        Assert.Equal(1, approved.Asked);
    }

    [Fact]
    public async Task WithNoConfirmationServiceNothingIsSent()
    {
        await Save("Omar");

        var result = await Executor(true).ExecuteAsync(Call("send_message", Args("Omar", "hi")), new ToolContext(Guid.NewGuid(), "text omar"));

        Assert.Equal(ToolResultStatus.Declined, result.Status);
        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public async Task TheUserIsShownWhoTheNameCameTo_WhereTheMessageWouldGo_AndTheWholeText_BeforeAnythingIsSent()
    {
        await Save("Omar Hassan", ["Brother"]);
        var asked = new FakeConfirmation(approve: false);

        await Executor(true, asked).ExecuteAsync(Call("send_message", Args("my brother", "I'm late, sorry\nSee you at 6")), new ToolContext(Guid.NewGuid(), "text my brother"));

        var question = Assert.Single(asked.Shown);
        Assert.Equal(ConfirmationKind.SendMessage, question.Kind);
        Assert.Equal("Send this message to Omar Hassan?", question.Title);
        Assert.Equal(
            [
                ("To", "Omar Hassan"),
                ("Through", "WhatsApp chat with Omar Hassan (a sample: nothing is really sent)"),
                ("Message", "I'm late, sorry\nSee you at 6"),
            ],
            question.Details.Select(line => (line.Label, line.Value)));
        Assert.Equal("Send", question.ApproveLabel);
        Assert.Contains("cannot be taken back", question.Warning, StringComparison.Ordinal);
        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public async Task WhatIsSentIsTheDraftTheUserApproved_ToTheSameChat_WithTheSameText()
    {
        await Save("Omar Hassan", ["Brother"]);
        var asked = new FakeConfirmation(approve: true);

        var result = await Executor(true, asked).ExecuteAsync(Call("send_message", Args("Omar", "  see you at 6  ")), new ToolContext(Guid.NewGuid(), "text omar"));

        Assert.Equal(ToolResultStatus.Succeeded, result.Status);

        // One draft was made, before the question, and it is that one that was sent: nothing was looked up a second time in between.
        Assert.Single(_provider.Drafted);
        var sent = Assert.Single(_provider.Sent);
        Assert.Equal("WhatsApp chat with Omar Hassan", sent.Route);
        Assert.Equal(asked.Shown[0].Details.Single(line => line.Label == "Message").Value, sent.Message.Text);
        Assert.Equal("see you at 6", sent.Message.Text);
    }

    [Fact]
    public async Task ANameThatFitsTwoPeople_OrNoOne_OrNoWayToReachThem_IsNotAskedAboutAndSendsNothing()
    {
        await Save("Omar Hassan", ["Brother"]);
        await Save("Sami Hassan", ["Brother"]);
        await Save("Lina", ["Cousin"], reachable: false);
        var asked = new FakeConfirmation(approve: true);
        var executor = Executor(true, asked);
        var context = new ToolContext(Guid.NewGuid(), "text my brother");

        var two = await executor.ExecuteAsync(Call("send_message", Args("my brother", "hi")), context);
        var nobody = await executor.ExecuteAsync(Call("send_message", Args("my uncle", "hi")), context);
        var unreachable = await executor.ExecuteAsync(Call("send_message", Args("Lina", "hi")), context);

        Assert.Contains("needs_clarification", two.OutputJson, StringComparison.Ordinal);
        Assert.Contains("person_not_found", nobody.OutputJson, StringComparison.Ordinal);
        Assert.Equal(ToolResultStatus.Failed, unreachable.Status);
        Assert.Equal(0, asked.Asked);
        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public async Task AMessageThatCouldNotBeSentAfterTheYesIsAFailureTheModelPassesOn()
    {
        await Save("Omar Hassan", ["Brother"]);
        var asked = new FakeConfirmation(approve: true);

        // The user said yes; the messaging app then refuses.
        var tool = new SendMessageTool(new RefusingProvider(_provider), new PersonResolver(_store));
        var result = await new ToolExecutor([tool], asked, new FakePermissions(true)).ExecuteAsync(
            Call("send_message", Args("Omar", "hi")), new ToolContext(Guid.NewGuid(), "text omar"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Contains("Nothing was sent", result.OutputJson, StringComparison.Ordinal);
    }

    // Takes a draft, and refuses to send it.
    private sealed class RefusingProvider(IMessagingProvider inner) : IMessagingProvider
    {
        public string Name => inner.Name;

        public bool IsSample => inner.IsSample;

        public Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken) => inner.CreateDraftAsync(message, cancellationToken);

        public Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken) =>
            throw new MessagingProviderException(MessagingFailure.Rejected);
    }

    [Fact]
    public async Task DraftingNeedsNoConfirmation_ButNeitherRunsWhileTheMessagingPermissionIsOff()
    {
        await Save("Omar");
        var asked = new FakeConfirmation(approve: true);

        var drafted = await Executor(true, asked).ExecuteAsync(Call("draft_message", Args("Omar", "hi")), new ToolContext(Guid.NewGuid(), "text omar"));
        Assert.Equal(ToolResultStatus.Succeeded, drafted.Status);
        Assert.Equal(0, asked.Asked);

        _provider.FailWith(null);
        var off = Executor(false, asked);
        foreach (var tool in new[] { "draft_message", "send_message" })
        {
            var refused = await off.ExecuteAsync(Call(tool, Args("Omar", "hi")), new ToolContext(Guid.NewGuid(), "text omar"));
            Assert.Equal(ToolResultStatus.Failed, refused.Status);
            Assert.Equal(ToolErrors.PermissionOff, Json(refused).GetProperty("code").GetString());
        }

        Assert.Empty(_provider.Sent);
        Assert.Single(_provider.Drafted);
    }

    [Fact]
    public async Task ASendWithAnArgumentTheToolDoesNotTakeIsRefusedBeforeItIsConfirmed()
    {
        await Save("Omar");
        var asked = new FakeConfirmation(approve: true);

        var result = await Executor(true, asked).ExecuteAsync(
            Call("send_message", """{"recipient":"Omar","text":"hi","phone":"+15550001111"}"""), new ToolContext(Guid.NewGuid(), "text omar"));

        Assert.Equal(ToolResultStatus.Failed, result.Status);
        Assert.Equal(ToolErrors.InvalidArguments, Json(result).GetProperty("code").GetString());
        Assert.Equal(0, asked.Asked);
        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public void ThePermissionTheToolsNeedCanBeAllowedAndStartsOffAndCanAskEachTime()
    {
        var messaging = Assistant.Core.Permissions.PermissionCatalog.Get(PermissionCapability.Messaging);

        Assert.Equal(PermissionAvailability.Available, messaging.Availability);
        Assert.True(messaging.SupportsAskEveryTime);
        Assert.False(new Assistant.Core.Settings.PermissionSettings().Messaging);
    }

    // ---- the sample provider ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheSampleSaysItIsOne_AndOnlyReachesAPersonWithSomethingToReachThemBy()
    {
        var recipient = new MessageRecipient(Guid.NewGuid(), "Omar", [new(PersonIdentifierKind.Username, "omar_h")]);
        var nobody = new MessageRecipient(Guid.NewGuid(), "Nobody", []);

        var draft = await _provider.CreateDraftAsync(new OutgoingMessage(recipient, "hi"), CancellationToken.None);
        var failure = await Assert.ThrowsAsync<MessagingProviderException>(() => _provider.CreateDraftAsync(new OutgoingMessage(nobody, "hi"), CancellationToken.None));

        Assert.True(_provider.IsSample);
        Assert.Equal("Sample chat with Omar", draft.Route);
        Assert.Equal(MessagingFailure.RecipientNotFound, failure.Failure);
        Assert.Empty(_provider.Sent);
    }

    [Fact]
    public void TheMessagePartsPrintWithoutWhatTheyHold()
    {
        var recipient = new MessageRecipient(Guid.NewGuid(), "Secret Name", [new(PersonIdentifierKind.Phone, Phone)]);
        var draft = new MessageDraft(new OutgoingMessage(recipient, "secret words"), "secret route", "secret-chat-id");

        var text = string.Join(" ", draft, draft.Message, recipient, new MessageSendResult(MessageDeliveryStatus.Sent, "secret route"));

        foreach (var secret in new[] { "Secret Name", "secret words", "secret route", "secret-chat-id", "7700" })
        {
            Assert.DoesNotContain(secret, text, StringComparison.Ordinal);
        }
    }

    private sealed class BrokenStore : IPersonStore
    {
        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<Person>> ListAsync(CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");

        public Task<Person?> GetAsync(Guid id, CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");

        public Task<Person> SaveAsync(Person person, CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) => throw new PersonStoreException("broken");
    }
}
