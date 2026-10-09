using System.Collections.Concurrent;
using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Core.People;
using Assistant.Core.Permissions;
using Assistant.Core.Tools;

namespace Assistant.Tools.Messaging;

/// <summary>
/// Which chat the user chose for a person in a conversation ("through Beeper"), kept until the message is sent: the draft is made with the choice, and
/// the send that follows it, which looks the route up again, takes the same one whether the model repeats the choice or not. Once the message has gone the
/// choice is spent: it was for that message, and the next one goes where the person's messages usually go. It is kept in memory only; what
/// lasts is the chat the user says they prefer, when they are asked after choosing (<see cref="IMessagingProvider.PreferAsync"/>).
/// </summary>
internal static class MessagingChoices
{
    private const int MaxRemembered = 64;
    private static readonly ConcurrentDictionary<(Guid Conversation, Guid Person), (string Words, bool IsUsersOwn)> Chosen = new();

    // The people the user was asked "which chat?" about in a conversation, until the chat they answered with is drafted to.
    private static readonly ConcurrentDictionary<(Guid Conversation, Guid Person), bool> Asked = new();

    /// <summary>Notes that the user is being asked which of <paramref name="person"/>'s chats to use.</summary>
    public static void NoteAsked(ToolContext context, Guid person)
    {
        if (Asked.Count >= MaxRemembered)
        {
            foreach (var key in Asked.Keys.Take(MaxRemembered / 4).ToList())
            {
                Asked.TryRemove(key, out _);
            }
        }

        Asked[(context.ConversationId, person)] = true;
    }

    /// <summary>
    /// Whether the user was asked which of <paramref name="person"/>'s chats to use in this conversation and has not been asked since whether their answer is
    /// the one they prefer. It is true once: asking takes it.
    /// </summary>
    public static bool TakeAsked(ToolContext context, Guid person) => Asked.TryRemove((context.ConversationId, person), out _);

    /// <summary>Forgets the choice for <paramref name="person"/> in this conversation: the message it was made for has been sent.</summary>
    public static void Spend(ToolContext context, Guid person) => Chosen.TryRemove((context.ConversationId, person), out _);

    /// <summary>
    /// The choice for <paramref name="person"/> in the conversation of <paramref name="context"/>: <paramref name="via"/> when the call gives one, and otherwise
    /// what an earlier call gave. It is the user's own when its letters and digits are in what the user wrote in the request the call belongs to.
    /// </summary>
    public static (string Words, bool IsUsersOwn) For(ToolContext context, Guid person, string via, bool saidByUser = false)
    {
        var key = (context.ConversationId, person);
        if (via.Length == 0)
        {
            return Chosen.TryGetValue(key, out var earlier) ? earlier : (string.Empty, false);
        }

        var said = Squash(via);
        var own = saidByUser || (said.Length >= 3 && Squash(context.Request).Contains(said, StringComparison.Ordinal));

        // The same words given again (the send after the draft, a turn later) are still the user's own.
        if (Chosen.TryGetValue(key, out var known) && Squash(known.Words) == said)
        {
            own |= known.IsUsersOwn;
        }

        if (Chosen.Count >= MaxRemembered)
        {
            foreach (var old in Chosen.Keys.Take(MaxRemembered / 4).ToList())
            {
                Chosen.TryRemove(old, out _);
            }
        }

        Chosen[key] = (via, own);
        return (via, own);
    }

    private static string Squash(string? text) => new((text ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}

/// <summary>
/// The messaging service a request names in the user's own words ("on iMessage", "through whatsapp"), however it was typed: "imesasge" is iMessage. It
/// says which of a person's chats the message goes to whether or not the model passed it on. A request that names two is left to the model.
/// </summary>
internal static class MessagingServiceWords
{
    /// <summary>The one service <paramref name="request"/> names, or <see langword="null"/> when it names none or more than one.</summary>
    public static string? Named(string? request)
    {
        var words = RequestWords.Of(request);
        if (words.Count == 0)
        {
            return null;
        }

        var named = MessagingServices.Words
            .Where(entry => entry.Key.Length >= 3 && (words.Contains(entry.Key) || (entry.Key.Length >= 5 && words.Any(word => RequestWords.IsSlip(word, entry.Key)))))
            .Select(entry => entry.Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return named.Count == 1 ? named[0] : null;
    }
}

/// <summary>
/// A message the model drafted and showed the user in words, for each conversation, until the user says something that is not a yes: their "yes" (or
/// "send it") is then the go-ahead for that message, and send_message is the only tool offered for it, so that a small model sends it and does nothing else.
/// </summary>
internal static class MessagingDrafts
{
    private const int MaxRemembered = 64;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Pending = new();

    private static readonly HashSet<string> YesWords = new(StringComparer.Ordinal)
    {
        "yes", "yeah", "yea", "yep", "yup", "ya", "y", "sure", "ok", "okay", "okey", "k", "send", "it", "go", "ahead", "do", "please", "pls", "confirm",
        "confirmed", "correct", "right", "sounds", "good", "great", "perfect", "absolutely", "definitely", "fine", "alright", "that", "now", "thanks", "thank", "you",
    };

    /// <summary>Notes that a draft was shown in the conversation.</summary>
    public static void Note(Guid conversation)
    {
        // Past the limit the drafts nobody is waiting on any more go first, then the oldest: a conversation that is waiting is never the one forgotten.
        if (Pending.Count >= MaxRemembered)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var old in Pending.Where(entry => now - entry.Value >= Lifetime).Select(entry => entry.Key).ToList())
            {
                Pending.TryRemove(old, out _);
            }

            if (Pending.Count >= MaxRemembered)
            {
                Pending.TryRemove(Pending.OrderBy(entry => entry.Value).Select(entry => entry.Key).First(), out _);
            }
        }

        Pending[conversation] = DateTimeOffset.UtcNow;
    }

    /// <summary>Forgets the conversation's draft.</summary>
    public static void Forget(Guid conversation) => Pending.TryRemove(conversation, out _);

    /// <summary>Whether a draft shown in the conversation a few minutes ago waits for the user's go-ahead.</summary>
    public static bool IsPending(Guid conversation) =>
        Pending.TryGetValue(conversation, out var at) && DateTimeOffset.UtcNow - at < Lifetime;

    /// <summary>Whether <paramref name="request"/> is only a yes: "yes", "send it", "ok go ahead".</summary>
    public static bool IsYes(string? request)
    {
        var words = RequestWords.Of(request);
        return words.Count is > 0 and <= 6 && words.All(YesWords.Contains) && !words.SetEquals(["it"]) && !words.SetEquals(["do"]);
    }
}

/// <summary>
/// The two messaging tools (PROJECT_SPEC §4.8, step 113): <c>draft_message</c>, which works out who a message is for and where it would go and sends nothing, and
/// <c>send_message</c>, which sends it. Both name the recipient the way the user does ("my brother", "Omar") and take only a person the user has saved (<see cref="IPersonResolver"/>,
/// over the Assistant's own list, so that what "my brother" means never depends on a messaging app or a connected app): never a number or an address the model has in front of it, so
/// that text from a page or a file cannot make the Assistant message a stranger. When no single person fits, nothing is drafted or sent and the model is told to ask the user. Sending
/// is a <see cref="RiskLevel.SideEffect"/>, so the executor asks the user to confirm every call (step 115): the question names the person the name was resolved to, where the message would
/// go and the whole text, and it is that draft, made once for exactly that recipient and text, that is sent on a yes. Both need
/// the Messaging permission, which today cannot be allowed (no real messaging app is connected), so in the app they never run. They are a capability and not an app: what sends is the
/// <see cref="IMessagingProvider"/> the app has. They are offered only while a provider is there and the request seems to be about messaging (<see cref="MessagingRequests"/>).
/// </summary>
/// <param name="provider">What sends the messages; without one the tool is never offered.</param>
/// <param name="people">Resolves "my brother" to a saved person; without it nothing can be sent, since no one can be named.</param>
/// <param name="permissions">
/// The permissions, when the tools should be offered only while Messaging is allowed (step 116): the question is asked once for a turn (<see cref="PrepareAsync"/>), together with
/// whether the provider has a messaging app at all (<see cref="IMessagingProvider.IsAvailableAsync"/>). Without it the permission is checked only when a call runs, as before.
/// </param>
/// <param name="questions">
/// What asks the user something in the conversation. With it, a user who was asked which of a person's chats to use and answered is then asked whether that
/// chat is the one they prefer for that person, before the message itself is shown to them; without it nobody is asked and nothing is kept.
/// </param>
public abstract class MessageTool(IMessagingProvider? provider, IPersonResolver? people, IPermissionPolicy? permissions = null, IPermissionService? questions = null) : ITool
{
    private const int MaxRememberedConversations = 64;

    // The question about the chat a person's messages usually go to is put to the user the way a tool's question is, in the conversation: this is what it
    // is asked as. It is not a tool, is never offered to the model and can never be "always allowed".
    private static readonly ToolDefinition PreferenceAsk = ToolDefinition.Create(
        "prefer_message_route", "Asks the user whether the chat they chose is the one they prefer for this person.", [], RiskLevel.SideEffect);

    // What was found out for each conversation's latest turn; a conversation with nothing found out is offered the tools as before.
    private readonly ConcurrentDictionary<Guid, bool> _ready = new();

    /// <inheritdoc/>
    public abstract ToolDefinition Definition { get; }

    /// <summary>The parameters both tools take.</summary>
    protected static ToolParameter[] Parameters(string recipientHelp, string textHelp) =>
    [
        new ToolParameter("recipient", ToolParameterType.String, recipientHelp, MaxLength: MessagingToolResults.MaxRecipientLength),
        new ToolParameter("text", ToolParameterType.String, textHelp, MaxLength: MessagingToolResults.MaxTextLength),
        new ToolParameter(
            "via", ToolParameterType.String,
            "Only after a result said the person has more than one chat, or none was found: the user's answer to which one, as they said it, such as iMessage, Beeper or " +
            "WhatsApp, or the handle or chat name they gave. Leave it out otherwise: the chat the user prefers is used.",
            Required: false, MaxLength: MessagingToolResults.MaxRecipientLength),
    ];

    /// <inheritdoc/>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // Before a turn has found out (a tool used on its own, as the tests and the older callers do), a tool made without the permissions is offered as it always was, and one made
        // with them waits to be told: a conversation that was never prepared is not offered a tool for a messaging app that may not be there.
        return provider is not null && MessagingConversations.IsAbout(context) && (_ready.TryGetValue(context.ConversationId, out var ready) ? ready : permissions is null);
    }

    /// <inheritdoc/>
    public bool IsFocused(ToolContext context) => IsOffered(context);

    /// <inheritdoc/>
    public virtual bool Claims(ToolContext context) => false;

    /// <summary>Whether a draft this tool made waits for the user's yes (draft_message, whose drafts are shown in words).</summary>
    protected virtual bool ShowsDraft => false;

    /// <inheritdoc/>
    /// <remarks>
    /// Finds out whether the tools are worth offering for this turn (step 116): the provider has a messaging app to send through, and, when the permissions are at hand, Messaging is
    /// allowed. A provider that reaches whichever messaging app the user has connected has none until one is, and the Messaging permission may be off; neither is worth the model's
    /// attention. A failure to find out leaves the tools as they were, since every call asks again.
    /// </remarks>
    public async Task PrepareAsync(ToolContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        MessagingConversations.Note(context);

        // Whether a message is being sent in this conversation, which keeps the messaging app's own tools from the model meanwhile.
        MessagingFlows.Note(context);

        // A draft waits for a yes only until the user says something else.
        if (!MessagingDrafts.IsYes(context.Request))
        {
            MessagingDrafts.Forget(context.ConversationId);
        }

        if (provider is null)
        {
            return;
        }

        var ready = await provider.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
        if (ready && permissions is not null)
        {
            // Set to ask every time counts: the tools are offered and the executor asks the user when one is called.
            ready = (await permissions.CheckAsync(PermissionCapability.Messaging, cancellationToken).ConfigureAwait(false)).CouldBeAllowed;
        }

        // A few conversations are remembered; past that the rest are forgotten, and a forgotten conversation is offered the tools until its next turn finds out again.
        if (_ready.Count >= MaxRememberedConversations)
        {
            _ready.Clear();
        }

        _ready[context.ConversationId] = ready;
    }

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (draft, failure) = await DraftAsync(call, arguments, context, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return failure;
        }

        var result = await CompleteAsync(call, draft!, cancellationToken).ConfigureAwait(false);
        if (result.Status == ToolResultStatus.Succeeded)
        {
            // A draft shown in words waits for a yes; a message that was sent leaves nothing waiting.
            if (ShowsDraft)
            {
                MessagingDrafts.Note(context.ConversationId);
            }
            else
            {
                MessagingDrafts.Forget(context.ConversationId);
                MessagingChoices.Spend(context, draft!.Message.Recipient.PersonId);
                MessagingFlows.End(context.ConversationId);
            }
        }

        return result;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The recipient is resolved and the draft made once, before the user is asked: a name that fits no one, or two people, is told to the model and nobody is
    /// asked, and the user is shown who the name came to, where the message would go and the whole text, and it is that draft that is sent on a yes.
    /// </remarks>
    public async Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var (draft, failure) = await DraftAsync(call, arguments, context, cancellationToken).ConfigureAwait(false);
        if (failure is not null)
        {
            return ToolPlan.Refuse(failure);
        }

        var made = draft!;
        var source = provider!;
        var name = made.Message.Recipient.DisplayName;
        var route = ConfirmationText.Short(made.Route, 200);

        // The user was asked which of this person's chats to use, and this is their answer: before the message, they are asked whether it is the one they
        // prefer. A chat they named themselves for this one message ("on iMessage") is not asked about, and changes nothing that is kept.
        if (made.Message.Via.Length > 0 && MessagingChoices.TakeAsked(context, made.Message.Recipient.PersonId))
        {
            await AskWhetherPreferredAsync(call, context, source, made, cancellationToken).ConfigureAwait(false);
        }

        // The user may rewrite the words before they say Send: what is sent is then what they left in the field, to the same chat.
        var edit = new ConfirmationEdit("Message", MessagingToolResults.MaxTextLength);
        return ToolPlan.Do(
            async token =>
            {
                var result = await CompleteAsync(call, Edited(made, edit), token).ConfigureAwait(false);
                if (result.Status == ToolResultStatus.Succeeded)
                {
                    // Sent: nothing waits for a yes any more, and the chat chosen for this message is not carried to the next.
                    MessagingDrafts.Forget(context.ConversationId);
                    MessagingChoices.Spend(context, made.Message.Recipient.PersonId);
                    MessagingFlows.End(context.ConversationId);
                }

                return result;
            },
            new ToolConfirmation(
                ConfirmationKind.SendMessage,
                $"Send this message to {ConfirmationText.Short(name, 40)}?",
                [
                    new ConfirmationDetail("To", name),
                    new ConfirmationDetail("Through", source.IsSample || made.IsSample ? $"{route} (a sample: nothing is really sent)" : route),
                    new ConfirmationDetail("Message", made.Message.Text),
                ],
                "Send",
                "A message cannot be taken back once it is sent.")
            {
                Edit = edit,
                DeclineLabel = "Cancel",
            });
    }

    // "Is Beeper your preferred way to message Sami?" Yes keeps the chat for that person; No keeps nothing, and they are asked which again next time. Either
    // way the message goes on to its own question. The run's clock stands still while the user reads.
    private async Task AskWhetherPreferredAsync(ToolCall call, ToolContext context, IMessagingProvider source, MessageDraft made, CancellationToken cancellationToken)
    {
        if (questions is null)
        {
            return;
        }

        var name = made.Message.Recipient.DisplayName;
        var service = made.Service.Length > 0 ? made.Service : made.App.Length > 0 ? made.App : source.Name;
        var question = new ToolConfirmation(
            ConfirmationKind.Other,
            $"Is {ConfirmationText.Short(service, 30)} your preferred way to message {ConfirmationText.Short(name, 40)}?",
            [
                new ConfirmationDetail("To", name),
                new ConfirmationDetail("Through", ConfirmationText.Short(made.Route, 200)),
            ],
            "Yes",
            $"Yes: messages to {ConfirmationText.Short(name, 40)} go through it from now on, unless you name another, such as “on WhatsApp”. " +
            "No: you are asked which one again next time. You can change this in Settings, under Memory.")
        {
            DeclineLabel = "No",
            ApprovedResult = "Kept. Their messages go through it from now on.",
            DeclinedResult = "Not kept. You are asked which one again next time.",
        };

        ConfirmationDecision decision;
        using (context.RunPause?.Pause())
        {
            decision = await questions.ConfirmToolCallAsync(PreferenceAsk, call, context, question, cancellationToken).ConfigureAwait(false);
        }

        if (decision == ConfirmationDecision.Approved)
        {
            await source.PreferAsync(made, cancellationToken).ConfigureAwait(false);
        }
    }

    // The draft with the words the user left in the question's field, when they changed them and left something to send.
    private static MessageDraft Edited(MessageDraft draft, ConfirmationEdit edit) =>
        edit.Value is { } changed && MessagingToolResults.CleanText(changed) is { Length: > 0 } text && text != draft.Message.Text
            ? draft with { Message = draft.Message with { Text = text } }
            : draft;

    // What the tool does with the draft (nothing, or send it), and what a provider that fails is told to the model as.
    private async Task<ToolResult> CompleteAsync(ToolCall call, MessageDraft draft, CancellationToken cancellationToken)
    {
        try
        {
            return await RunAsync(call, provider!, draft, cancellationToken).ConfigureAwait(false);
        }
        catch (MessagingProviderException failure)
        {
            return Failed(call, Explain(failure, draft.Message.Recipient.DisplayName, provider!.Name));
        }
    }

    // Who the message is for and where it would go, made once; or why there is none, as the result of the call. The route is looked up and checked for every call,
    // whether it drafts or sends: a send never relies on a draft from an earlier turn.
    private async Task<(MessageDraft? Draft, ToolResult? Failure)> DraftAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        // The model may call a registered tool that was not offered, so there may be no provider.
        if (provider is null)
        {
            return (null, Failed(call, "No messaging app is connected, so nothing can be drafted or sent. Tell the user."));
        }

        // The permission is checked here too, in the tool's own code, and not only by the executor that runs it: nothing is looked up in a messaging app while it is off,
        // or while it is set to ask and this use was not approved. (When it is allowed, or the user has just said yes, the check passes.)
        if (permissions is not null && await permissions.CheckAsync(PermissionCapability.Messaging, cancellationToken).ConfigureAwait(false) is { IsAllowed: false } decision)
        {
            return (null, Failed(call, PermissionTexts.ForModel(decision)));
        }

        if (people is null)
        {
            return (null, Failed(call, "The Assistant has no list of people to message. Tell the user."));
        }

        var text = MessagingToolResults.CleanText(Text(arguments, "text"));
        if (text.Length == 0)
        {
            return (null, ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.InvalidArguments, "There is no message to send. Give what the message says.", ToolUsage.Describe(Definition)));
        }

        PersonResolution resolution;
        try
        {
            resolution = await people.ResolveAsync(Text(arguments, "recipient"), cancellationToken).ConfigureAwait(false);
        }
        catch (PersonStoreException)
        {
            return (null, Failed(call, "The people the user saved could not be read right now. Tell the user, or try again."));
        }

        if (resolution.Person is not { } person)
        {
            return (null, new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, MessagingToolResults.NoRecipient(resolution, Text(arguments, "recipient"))));
        }

        // Which of the person's chats, when the user was asked and said: what this call gives, or what an earlier call of this conversation gave for the same person, so
        // that a draft made through the chat the user chose is sent through it too.
        // The service the user named in the request itself ("on iMessage") is the one, whether the model passed it on, left it out or passed another on; a
        // handle or a chat's name the model passes on (the user's answer to "which one?") is kept as it is.
        var passed = Clean(Text(arguments, "via"));
        var named = MessagingServiceWords.Named(context.Request);
        var saidByUser = named is not null && !ConnectedApps.McpMessagingProvider.LooksLikeHandle(passed)
            && !string.Equals(MessagingServices.Find(passed), named, StringComparison.OrdinalIgnoreCase);
        var via = MessagingChoices.For(context, person.Id, saidByUser ? named! : passed, saidByUser);
        var message = new OutgoingMessage(MessageRecipient.From(person), text) { Via = via.Words, ViaIsUsersOwn = via.IsUsersOwn };
        try
        {
            return (await provider.CreateDraftAsync(message, cancellationToken).ConfigureAwait(false), null);
        }
        catch (MessagingProviderException failure)
        {
            if (failure.Failure == MessagingFailure.Ambiguous)
            {
                // The user is about to be asked which chat: their answer is then asked about once more, as the one they may prefer.
                MessagingChoices.NoteAsked(context, person.Id);
            }

            return (null, Failed(call, Explain(failure, person.DisplayName, provider.Name)));
        }
    }

    /// <summary>What the tool does once the provider has found the way to send the message.</summary>
    protected abstract Task<ToolResult> RunAsync(ToolCall call, IMessagingProvider source, MessageDraft draft, CancellationToken cancellationToken);

    /// <summary>The text of the argument <paramref name="name"/>, or <see langword="null"/> when it was not given.</summary>
    protected static string? Text(JsonElement arguments, string name) =>
        arguments.ValueKind == JsonValueKind.Object && arguments.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A failed result that says <paramref name="message"/> to the model.</summary>
    protected static ToolResult Failed(ToolCall call, string message) => ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, message);

    private static string Clean(string? text) => string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // What went wrong, in words the model passes on. It names the person (the user's own list) and never an address or what the message says.
    private static string Explain(MessagingProviderException failure, string person, string provider) => failure.Failure switch
    {
        MessagingFailure.SignInNeeded =>
            $"{provider} needs the user to sign in or to allow the Assistant, and the Assistant cannot do that for them. Nothing was sent. Tell the user.",
        MessagingFailure.RecipientNotFound when failure.Options.Count > 0 =>
            $"{provider} has no chat named like {person}, but it has chats with one person called: {string.Join("; ", failure.Options)}. Nothing was sent. Ask the user which of them is the person " +
            "(or what else they are called in the app); when they answer with a chat name, call remember_person with exactly that chat name and try again, and when they answer with a " +
            "handle or a username, call this tool again with via set to exactly what they gave.",
        MessagingFailure.RecipientNotFound =>
            $"{provider} found no chat with one person named like {person}. Nothing was sent. Ask the user how that person is spelt in {provider} (the name of their chat) or for their " +
            "handle there: for a name call remember_person with it and try again, and for a handle or a username call this tool again with via set to exactly what they gave.",
        MessagingFailure.Ambiguous =>
            $"{provider} has more than one chat with {person}" + (failure.Options.Count > 0 ? $" ({string.Join(", ", failure.Options.Take(5))})" : string.Empty)
            + ". Nothing was sent. Ask the user which one to use, and when they answer call this tool again with the same person and text and with via set to their answer" +
            (failure.Options.Count > 0 ? $", such as {failure.Options[0]}" : string.Empty) + ". They are then asked on screen whether that is the one they prefer: do not ask that yourself.",
        MessagingFailure.ServiceNotFound =>
            $"{provider} found no {failure.Service} chat with {person}" + (failure.Options.Count > 0 ? $"; the chats it found with them are on: {string.Join(", ", failure.Options.Take(5))}" : string.Empty)
            + $". Nothing was sent. Tell the user that, and ask whether to send it through one of those instead, or what their {failure.Service} chat is called in {provider}; "
            + "when they answer, call this tool again with via set to their answer.",
        MessagingFailure.Rejected => $"{provider} did not accept the message. Nothing was sent. Tell the user.",
        MessagingFailure.NotConnected =>
            "No messaging app is connected, so nothing can be sent. Nothing was sent. Tell the user they can connect one, and that I can look for one if they ask.",
        MessagingFailure.NotAllowed =>
            "Messaging is not allowed right now in Settings, under Permissions. Nothing was looked up and nothing was sent. Tell the user.",
        _ => $"{provider} could not be reached right now. Nothing was sent. Tell the user, or try again.",
    };
}

/// <summary><c>draft_message</c>: who a message is for and where it would go, with what it says. Nothing is sent and nothing is left in the messaging app.</summary>
public sealed class DraftMessageTool(IMessagingProvider? provider, IPersonResolver? people, IPermissionPolicy? permissions = null) : MessageTool(provider, people, permissions, null)
{
    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        MessagingToolResults.DraftMessage,
        "Prepare a message to someone the user has saved without sending it: only when the user asks for a draft and does not want it sent yet. To send a " +
        "message, call send_message instead, which shows the user the message with a Send button. Nothing is sent by this.",
        Parameters(
            "Who the message is for, the way the user said it: a saved person's name, such as Omar, or how they relate to the user, such as my brother.",
            "Exactly the words of the message, as the user said them. Do not add anything."),
        RiskLevel.ReadOnly,
        PermissionCapability.Messaging,
        TimeSpan.FromSeconds(30));

    /// <inheritdoc/>
    protected override bool ShowsDraft => true;

    /// <inheritdoc/>
    protected override Task<ToolResult> RunAsync(ToolCall call, IMessagingProvider source, MessageDraft draft, CancellationToken cancellationToken) =>
        Task.FromResult(new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, MessagingToolResults.Drafted(source.Name, source.IsSample || draft.IsSample, draft)));
}

/// <summary><c>send_message</c>: sends a message to someone the user has saved. The user confirms every call.</summary>
public sealed class SendMessageTool(IMessagingProvider? provider, IPersonResolver? people, IPermissionPolicy? permissions = null, IPermissionService? questions = null)
    : MessageTool(provider, people, permissions, questions)
{
    /// <inheritdoc/>
    public override ToolDefinition Definition { get; } = ToolDefinition.Create(
        MessagingToolResults.SendMessage,
        "Send a message to someone the user has saved, through their messaging app. Use it whenever the user asks you to message, text or tell someone and has said what to " +
        "say, and when they say yes to a draft: the user is shown the message with Send and Cancel and decides there, so do not ask them in words first. Only saved people " +
        "can be messaged.",
        Parameters(
            "Who the message is for, the way the user said it: a saved person's name, such as Omar, or how they relate to the user, such as my brother.",
            "Exactly the words to send, as the user said them. Do not add anything."),
        RiskLevel.SideEffect,
        PermissionCapability.Messaging,
        TimeSpan.FromSeconds(60));

    /// <inheritdoc/>
    /// <inheritdoc/>
    /// <remarks>The user's yes to a draft the model showed in words is send_message's alone: nothing else is offered for it.</remarks>
    public override bool Claims(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return MessagingDrafts.IsPending(context.ConversationId) && MessagingDrafts.IsYes(context.Request) && IsOffered(context);
    }

    /// <inheritdoc/>
    protected override async Task<ToolResult> RunAsync(ToolCall call, IMessagingProvider source, MessageDraft draft, CancellationToken cancellationToken)
    {
        var result = await source.SendAsync(draft, cancellationToken).ConfigureAwait(false);
        return new ToolResult(call.Id, call.ToolName, ToolResultStatus.Succeeded, MessagingToolResults.Sent(source.Name, source.IsSample || draft.IsSample, draft.Message.Recipient, result, draft.Message.Text));
    }
}
