using System.Text;
using Assistant.Core.People;

namespace Assistant.Core.Messaging;

/// <summary>Who a message is for: a saved person, and the addresses a provider may reach them by.</summary>
/// <param name="PersonId">The saved person's id.</param>
/// <param name="DisplayName">The name the user knows them by.</param>
/// <param name="Identifiers">Their phone numbers, email addresses and usernames, which the provider matches against its own chats.</param>
public sealed record MessageRecipient(Guid PersonId, string DisplayName, IReadOnlyList<PersonIdentifier> Identifiers)
{
    /// <summary>The other names the person is called ("Bro"), which a provider may look their chat up by when they have no number, address or username.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>The recipient a saved person makes.</summary>
    public static MessageRecipient From(Person person)
    {
        ArgumentNullException.ThrowIfNull(person);
        return new MessageRecipient(person.Id, person.DisplayName, person.Identifiers) { Aliases = person.Aliases };
    }

    // Keeps the name and the addresses (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"PersonId = {PersonId}");
        return true;
    }
}

/// <summary>A message the user wants sent, before anything is.</summary>
/// <param name="Recipient">Who it is for.</param>
/// <param name="Text">What it says: plain text.</param>
public sealed record OutgoingMessage(MessageRecipient Recipient, string Text)
{
    /// <summary>
    /// Which of the person's chats the user chose, in their words: a messaging service ("iMessage"), or the handle or the name of the chat. Empty when they did
    /// not say, and then a chat they chose before is used, or the only one there is.
    /// </summary>
    public string Via { get; init; } = string.Empty;

    /// <summary>
    /// Whether the words of <see cref="Via"/> are in what the user themselves wrote or said in this request. Only then may a provider look a chat up by them; words
    /// that are the model's alone can only choose among the chats the person already has, so that text from a page or a file cannot add someone to message.
    /// </summary>
    public bool ViaIsUsersOwn { get; init; }

    // Keeps what the message says (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Recipient = {Recipient}");
        return true;
    }
}

/// <summary>
/// A message and the way a provider found to send it (PROJECT_SPEC §4.8, step 113): made by <see cref="IMessagingProvider.CreateDraftAsync"/>, which
/// sends nothing, and the only thing <see cref="IMessagingProvider.SendAsync"/> accepts, so that what is sent is what was drafted, to where it was
/// said it would go.
/// </summary>
/// <param name="Message">The message.</param>
/// <param name="Route">Where it will go, in words the user reads: "WhatsApp chat with Omar".</param>
/// <param name="Reference">What the provider needs to send it (such as a chat's id). Opaque to everything but the provider that made it, and never shown.</param>
/// <param name="IsSample">
/// Whether the messaging app this draft would go through is made up for trying the Assistant (step 116), so that nothing sent reaches anyone. A provider that is one app says it with
/// <see cref="IMessagingProvider.IsSample"/>; one that reaches whichever messaging apps are connected says it for each draft.
/// </param>
public sealed record MessageDraft(OutgoingMessage Message, string Route, string Reference, bool IsSample = false)
{
    /// <summary>The messaging service the chat is on ("iMessage", "WhatsApp"), when the provider knows; empty otherwise. It is what the message is shown as going through.</summary>
    public string Service { get; init; } = string.Empty;

    /// <summary>The messaging app that sends it ("Beeper"), when it is not the provider itself; empty otherwise.</summary>
    public string App { get; init; } = string.Empty;

    // Keeps the message and the route (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Message = {Message}");
        return true;
    }
}

/// <summary>How far a sent message got.</summary>
public enum MessageDeliveryStatus
{
    /// <summary>The messaging app took the message and sent it.</summary>
    Sent = 0,

    /// <summary>The messaging app took the message and is sending it; it has not said that it arrived.</summary>
    Pending = 1,
}

/// <summary>What a provider says of a message it was asked to send.</summary>
/// <param name="Status">How far it got.</param>
/// <param name="Route">Where it went, in words the user reads.</param>
public sealed record MessageSendResult(MessageDeliveryStatus Status, string Route)
{
    /// <summary>The messaging service the message went through ("iMessage", "WhatsApp"), when the provider knows; empty otherwise.</summary>
    public string Service { get; init; } = string.Empty;

    /// <summary>The messaging app that sent it ("Beeper"), when it is not the provider itself; empty otherwise.</summary>
    public string App { get; init; } = string.Empty;

    // Keeps the route (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Status = {Status}");
        return true;
    }
}

/// <summary>Why a provider could not draft or send a message.</summary>
public enum MessagingFailure
{
    /// <summary>The messaging app could not be reached: it is not running, or it did not answer.</summary>
    Unavailable = 0,

    /// <summary>The messaging app needs the user to sign in, or to allow the Assistant, which the Assistant cannot do for them.</summary>
    SignInNeeded = 1,

    /// <summary>No chat with the person was found among the numbers, addresses and usernames saved for them.</summary>
    RecipientNotFound = 2,

    /// <summary>More than one chat with the person fits, and the provider does not guess between them.</summary>
    Ambiguous = 3,

    /// <summary>The messaging app refused the message.</summary>
    Rejected = 4,

    /// <summary>No messaging app is connected (step 116): there is nothing to ask, and the user can connect one.</summary>
    NotConnected = 5,

    /// <summary>The Messaging permission does not allow it (step 119): it is turned off, or set to ask every time and this use was not asked about. Nothing was looked up in the app.</summary>
    NotAllowed = 6,

    /// <summary>The user named the service the message should go through (iMessage), and the person has no chat on it among the ones found.</summary>
    ServiceNotFound = 7,
}

/// <summary>A provider could not draft or send a message.</summary>
public sealed class MessagingProviderException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="failure">Why.</param>
    /// <param name="options">For <see cref="MessagingFailure.Ambiguous"/>, what the chats are (the services they are on), for the question to the user.</param>
    /// <param name="inner">The cause, if there is one.</param>
    public MessagingProviderException(MessagingFailure failure, IReadOnlyList<string>? options = null, Exception? inner = null)
        : base($"The messaging provider could not do that ({failure}).", inner)
    {
        Failure = failure;
        Options = options ?? [];
    }

    /// <summary>Why the provider could not do it.</summary>
    public MessagingFailure Failure { get; }

    /// <summary>When the failure is <see cref="MessagingFailure.Ambiguous"/>, the services of the chats that fit.</summary>
    public IReadOnlyList<string> Options { get; }

    /// <summary>For <see cref="MessagingFailure.ServiceNotFound"/>, the service that was asked for; empty otherwise.</summary>
    public string Service { get; init; } = string.Empty;
}

/// <summary>
/// Something that can send the user's messages (PROJECT_SPEC §4.8, step 113): a messaging app on this PC, reached by the route the app itself offers
/// (its official local API, preferably through the generic MCP system), or a sample. It is a capability and not an app: the draft and send tools work
/// on whichever provider the app has, so a messaging app is added or replaced without the tools or the model changing. A provider never decides
/// who "my brother" is (that is <see cref="IPersonResolver"/>, over the user's own list), never sends without being handed a draft, and never
/// logs a name, an address or a message.
/// </summary>
public interface IMessagingProvider
{
    /// <summary>The provider's name as the user knows it: "Beeper".</summary>
    string Name { get; }

    /// <summary>Whether it is made up for trying the Assistant, so that nothing it sends reaches anyone.</summary>
    bool IsSample { get; }

    /// <summary>
    /// Whether the provider has a messaging app to send through right now (step 116). A provider that reaches whichever messaging app the user has connected says <see langword="false"/>
    /// while none is, so that the tools that use it are not offered to the model for nothing; the default is <see langword="true"/>, for a provider that is one app and always there.
    /// </summary>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(true);

    /// <summary>
    /// Finds the way to send <paramref name="message"/> and says where it would go, without sending anything or leaving anything in the messaging
    /// app (no draft is placed there): the route is looked up and checked, and nothing more.
    /// </summary>
    /// <exception cref="MessagingProviderException">The recipient cannot be reached by this provider, or the messaging app cannot be.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken);

    /// <summary>
    /// Sends the message in <paramref name="draft"/>, which this provider made, to where the draft says. The caller has the user's confirmation
    /// (the executor asks it of every call of a tool that sends).
    /// </summary>
    /// <exception cref="MessagingProviderException">The message could not be sent.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken);

    /// <summary>
    /// Keeps the chat <paramref name="draft"/> goes to as the one this person's messages go to from now on: the user was asked which of the person's
    /// chats to use, answered, and said that it is the one they prefer. A message that names no chat then goes there without a question; one that
    /// names another ("on WhatsApp") still goes where it says, and changes nothing that is kept. A provider with only one way to reach a person
    /// keeps nothing and says <see langword="false"/>, which is the default.
    /// </summary>
    /// <returns>Whether it was kept.</returns>
    Task<bool> PreferAsync(MessageDraft draft, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
