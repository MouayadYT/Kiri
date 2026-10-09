using Assistant.Core.Domain;

namespace Assistant.Core.Contracts;

/// <summary>How the Assistant answered a request for something of an external app without asking the model (PROJECT_SPEC §4.8, steps 105-106).</summary>
public enum ConnectedAppReplyKind
{
    /// <summary>The app is installed but cannot be used now (it is off, needs a sign-in, cannot be reached, or the user has a permission off).</summary>
    InstalledNotUsable = 0,

    /// <summary>The app is set up in another program on this PC but not in the Assistant, so nothing was looked up.</summary>
    FoundOnThisPc = 1,

    /// <summary>No integration is installed, and looking for one was not allowed (Local Only mode, or the web permission is off).</summary>
    DiscoveryBlocked = 2,

    /// <summary>No integration is installed; the Integration Finder looked and found candidates.</summary>
    DiscoveryFound = 3,

    /// <summary>No integration is installed; the Integration Finder looked and found nothing that fits.</summary>
    DiscoveryEmpty = 4,

    /// <summary>No integration is installed; the Integration Finder could not look (the places that list integrations did not answer).</summary>
    DiscoveryFailed = 5,

    /// <summary>The request asks for something the Assistant refuses to do in a connected app (deleting).</summary>
    Refused = 6,

    /// <summary>No integration is installed; the Integration Finder found candidates, the Assistant reviewed them, and one passed: it is offered for the user to approve (step 107-108).</summary>
    InstallOffered = 7,

    /// <summary>No integration is installed; candidates were found and reviewed, and none passed the review.</summary>
    NothingPassedReview = 8,

    /// <summary>The user stopped the Assistant while it looked for an integration (step 110): nothing was installed and the request was not carried out.</summary>
    LookupStopped = 9,

    /// <summary>The app is one whose server the Assistant knows, and it is not connected (or needs a sign-in again): connecting it is offered for the user to approve.</summary>
    ConnectOffered = 10,
}

/// <summary>The Assistant's own answer to a request that the model must not answer, in plain words.</summary>
/// <param name="Text">What to say to the user. It is written by the Assistant from facts it checked; nothing the model or the web wrote is repeated unchecked.</param>
/// <param name="Kind">How the request was handled.</param>
/// <param name="Offer">
/// An integration the Assistant offers to install, for the user to approve or turn down, or <see langword="null"/>. Offering it installs nothing:
/// only the user's click on Install does, and the model cannot make it.
/// </param>
/// <param name="NotInstalledText">
/// With an offer: what the Assistant says if the user does not install it (step 110): that it did not install the integration and so did not do
/// what was asked. The request is set aside until the user decides, and goes on automatically when the integration is installed. Written by the
/// Assistant from the app and the two words of the capability only, never from what the user wrote.
/// </param>
public sealed record ConnectedAppReply(string Text, ConnectedAppReplyKind Kind, IntegrationOffer? Offer = null, string? NotInstalledText = null);

/// <summary>
/// Decides whether a request is for something of an external app (adding a task to Microsoft To Do, say) that the Assistant cannot do now, and
/// if so answers it itself (PROJECT_SPEC §4.8, steps 105-106). The model is never asked in that case: a small model that is not given the
/// app's tools tends to say it did the thing. A request the installed integrations can serve, or that is not about an external app at all,
/// is not answered here, and the turn goes on to the model as it always did. When an integration is offered, the request is set aside
/// (<see cref="PendingRequest"/>) and goes on by itself once the user has installed it (step 110).
/// </summary>
public interface IConnectedAppRequestHandler
{
    /// <summary>
    /// The Assistant's own answer to the turn's request, or <see langword="null"/> when the model is to answer it. Nothing in here fails the
    /// turn: whatever goes wrong while checking is a request that is not handled.
    /// </summary>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled before the Assistant began to look for an integration. A stop after that is answered
    /// (<see cref="ConnectedAppReplyKind.LookupStopped"/>): it says that nothing was installed and the request was not carried out.
    /// </exception>
    Task<ConnectedAppReply?> TryAnswerAsync(ToolContext context, CancellationToken cancellationToken = default);
}
