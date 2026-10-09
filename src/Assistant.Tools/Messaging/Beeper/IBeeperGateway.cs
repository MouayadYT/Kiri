using Assistant.Core.People;

namespace Assistant.Tools.Messaging.Beeper;

/// <summary>A one-to-one chat in Beeper that a person may be reached in.</summary>
/// <param name="Id">Beeper's id for the chat, which only the gateway uses to send.</param>
/// <param name="Title">The chat's name in Beeper.</param>
/// <param name="Network">The messaging service the chat is on, as Beeper names it: "WhatsApp", "Signal".</param>
public sealed record BeeperChat(string Id, string Title, string Network)
{
    // Keeps the chat's name (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Network = {Network}");
        return true;
    }
}

/// <summary>Who to look for in Beeper: the name the user knows a person by, and the addresses saved for them.</summary>
/// <param name="DisplayName">The saved person's name.</param>
/// <param name="Identifiers">Their saved numbers, addresses and usernames.</param>
public sealed record BeeperChatQuery(string DisplayName, IReadOnlyList<PersonIdentifier> Identifiers)
{
    // Keeps the name and the addresses (private content, PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Identifiers = {Identifiers.Count}");
        return true;
    }
}

/// <summary>Why the gateway could not do what it was asked.</summary>
public enum BeeperGatewayFailure
{
    /// <summary>Beeper Desktop is not running, its local API is turned off, or it did not answer.</summary>
    NotAvailable = 0,

    /// <summary>Beeper has not been allowed to talk to the Assistant: no access was granted, or it was taken back.</summary>
    NotAuthorized = 1,

    /// <summary>Beeper refused the message, or the chat is gone.</summary>
    Rejected = 2,
}

/// <summary>The gateway could not reach Beeper or Beeper refused.</summary>
/// <param name="failure">Why.</param>
/// <param name="inner">The cause, if there is one.</param>
public sealed class BeeperGatewayException(BeeperGatewayFailure failure, Exception? inner = null)
    : Exception($"Beeper could not do that ({failure}).", inner)
{
    /// <summary>Why the gateway could not do it.</summary>
    public BeeperGatewayFailure Failure { get; } = failure;
}

/// <summary>
/// The one place the Assistant's messaging meets Beeper (PROJECT_SPEC §4.8, step 113): two questions that a messaging provider needs answered, and nothing
/// else. It is a boundary, not a connector. Beeper Desktop has an official local API with a built-in MCP server (on this PC only, over Streamable HTTP,
/// authorised with OAuth or an access token the user creates in Beeper), so the implementation of this interface belongs on the Assistant's generic MCP
/// subsystem (step 104) and not on a connector written for Beeper, and no user-interface automation is involved: the model is never given control of Beeper's
/// window, and nothing here clicks or types in it. That implementation is not part of this step.
/// </summary>
public interface IBeeperGateway
{
    /// <summary>
    /// The one-to-one chats that fit <paramref name="query"/>: a chat is a fit when its other member is reachable by one of the saved identifiers. Group chats are never
    /// returned: a message to one person must not go to a group.
    /// </summary>
    /// <exception cref="BeeperGatewayException">Beeper could not be asked.</exception>
    Task<IReadOnlyList<BeeperChat>> FindDirectChatsAsync(BeeperChatQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Sends <paramref name="text"/> as plain text to the chat <paramref name="chatId"/>. Returns whether Beeper has confirmed the message was sent
    /// (<see langword="false"/> when Beeper only queued it).
    /// </summary>
    /// <exception cref="BeeperGatewayException">Beeper could not be asked, or refused.</exception>
    Task<bool> SendTextAsync(string chatId, string text, CancellationToken cancellationToken);
}
