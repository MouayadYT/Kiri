using System.Text;

namespace Assistant.Core.Domain;

/// <summary>How a request that was set aside while the user decided on an integration came out (PROJECT_SPEC §4.8, step 110).</summary>
public enum PendingOutcome
{
    /// <summary>The integration the request needed is installed: the Assistant goes back to the request and carries it out.</summary>
    Installed = 0,

    /// <summary>The user did not install it (turned it down, cancelled, or the installation failed): the request is not carried out, and the Assistant says so.</summary>
    NotInstalled = 1,
}

/// <summary>
/// The request the Assistant set aside while the user decides whether to install an integration it needs (PROJECT_SPEC §4.8, step 110): "Add 'buy milk'
/// to Microsoft To Do" with no integration for it is answered with an offer to install one, and what the user decides goes back to this request, so
/// that they never have to say it again. It names the request's message and the Assistant's reply that holds the offer in the conversation, by
/// id, and holds the Assistant's own words for the case that the integration is not installed. It holds nothing the user wrote: the request itself
/// stays in the conversation, as a message like any other.
/// </summary>
/// <param name="RequestMessageId">The user's message that asked for it.</param>
/// <param name="ReplyMessageId">The Assistant's reply to it, which holds the offer: what the model remembers of the conversation drops it once the user has decided.</param>
/// <param name="NotInstalledText">What the Assistant says when the integration is not installed: that nothing was done, and why.</param>
public sealed record PendingRequest(Guid RequestMessageId, Guid ReplyMessageId, string NotInstalledText)
{
    // Keeps the words out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"RequestMessageId = {RequestMessageId}, ReplyMessageId = {ReplyMessageId}");
        return true;
    }
}
