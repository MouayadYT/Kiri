using Assistant.Core.Messaging;
using Assistant.Core.People;

namespace Assistant.Tools.Messaging.Beeper;

/// <summary>
/// An <see cref="IMessagingProvider"/> that sends through Beeper by way of an <see cref="IBeeperGateway"/> (PROJECT_SPEC §4.8, step 113): it only translates between the
/// Assistant's messages and the gateway's two questions, and decides nothing about who is meant (that is the user's own list of people). It drafts by finding the one
/// one-to-one chat for the person and sends nothing; it never guesses between chats, never sends to a group, and sends only a draft it made. When every address a person has is saved
/// for a service (WhatsApp, Signal), only chats on those services are considered; more than one chat is then a question for the user, not a choice. The app registers none today: the
/// gateway that talks to Beeper is a later step.
/// </summary>
/// <param name="gateway">Where Beeper is asked.</param>
public sealed class BeeperMessagingProvider(IBeeperGateway gateway) : IMessagingProvider
{
    /// <inheritdoc/>
    public string Name => "Beeper";

    /// <inheritdoc/>
    public bool IsSample => false;

    /// <inheritdoc/>
    public async Task<MessageDraft> CreateDraftAsync(OutgoingMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var recipient = message.Recipient;

        // Without an address there is nothing to match a chat by, and a name alone is not enough to be sure it is the right person.
        if (recipient.Identifiers.Count == 0)
        {
            throw new MessagingProviderException(MessagingFailure.RecipientNotFound);
        }

        IReadOnlyList<BeeperChat> found;
        try
        {
            found = await gateway.FindDirectChatsAsync(new BeeperChatQuery(recipient.DisplayName, recipient.Identifiers), cancellationToken).ConfigureAwait(false);
        }
        catch (BeeperGatewayException failure)
        {
            throw Translate(failure);
        }

        var chats = OnTheSavedServices(found.GroupBy(chat => chat.Id, StringComparer.Ordinal).Select(group => group.First()).ToList(), recipient);
        switch (chats.Count)
        {
            case 0:
                throw new MessagingProviderException(MessagingFailure.RecipientNotFound);
            case > 1:
                throw new MessagingProviderException(
                    MessagingFailure.Ambiguous, [.. chats.Select(chat => chat.Network).Where(network => network.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)]);
        }

        var chat = chats[0];
        var service = chat.Network.Length > 0 ? chat.Network : "Beeper";
        return new MessageDraft(message, $"{service} chat with {recipient.DisplayName} (in Beeper)", chat.Id);
    }

    /// <inheritdoc/>
    public async Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        if (string.IsNullOrWhiteSpace(draft.Reference) || string.IsNullOrWhiteSpace(draft.Message.Text))
        {
            throw new MessagingProviderException(MessagingFailure.Rejected);
        }

        try
        {
            var confirmed = await gateway.SendTextAsync(draft.Reference, draft.Message.Text, cancellationToken).ConfigureAwait(false);
            return new MessageSendResult(confirmed ? MessageDeliveryStatus.Sent : MessageDeliveryStatus.Pending, draft.Route);
        }
        catch (BeeperGatewayException failure)
        {
            throw Translate(failure);
        }
    }

    // When every address the user saved for the person is for a service (a WhatsApp number, a Signal username), only the chats on those services count: the user
    // chose which to keep. One address that is for no service in particular means any service will do, so every chat counts.
    private static List<BeeperChat> OnTheSavedServices(List<BeeperChat> chats, MessageRecipient recipient)
    {
        var services = recipient.Identifiers.Select(identifier => PersonText.Fold(identifier.Service)).ToList();
        if (services.Contains(string.Empty))
        {
            return chats;
        }

        var allowed = services.ToHashSet(StringComparer.Ordinal);
        return [.. chats.Where(chat => allowed.Contains(PersonText.Fold(chat.Network)))];
    }

    private static MessagingProviderException Translate(BeeperGatewayException failure) =>
        new(
            failure.Failure switch
            {
                BeeperGatewayFailure.NotAuthorized => MessagingFailure.SignInNeeded,
                BeeperGatewayFailure.Rejected => MessagingFailure.Rejected,
                _ => MessagingFailure.Unavailable,
            },
            inner: failure);
}
