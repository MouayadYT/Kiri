using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Ipc;
using Assistant.UI.Messages;
using Assistant.UI.Selection;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Placement;
using Assistant.Core.Permissions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UI.Browser;

/// <summary>
/// What happens when a browser sends the text its user selected, by choosing the Assistant's entry in the right-click menu (PROJECT_SPEC
/// §4.5): the floating Ask panel opens as a new conversation with that text attached, along with the title and address of the page and the
/// browser's name, the quick actions listed above the composer and the keyboard in it. Nothing is asked: the user picks an action or types
/// a question, and sends it. The panel opens beside the browser's window (inside it, against its right edge, away from the pointer), as the
/// answer does in the reference, rather than at the Assistant's usual place; the user can still drag it, and open the conversation in the
/// History window.
/// </summary>
/// <remarks>
/// <para>
/// Only the words the user selected are context, and, when the user chose "Selection + Nearby Context" in the extension's options
/// (step 88), a bounded stretch of the page's text just before and after them, which the panel says it holds. The rest of the page is never
/// read, so a question about "this page" has nothing but those words, and the page's title and address ride with them to say where they are
/// from. The text and the page's details stay in memory, on the conversation's chip, until the user takes it off or another conversation
/// replaces it; they are never saved, and never logged.
/// </para>
/// <para>
/// The selection is the user's own act in their browser, but it is still the Selected Text permission's text: when that is off nothing is
/// attached, and the panel says why.
/// </para>
/// </remarks>
internal sealed class AskBrowserSelectionController
{
    /// <summary>What the user is told when Selected Text is turned off in the Settings.</summary>
    internal const string TurnedOffText =
        "Selected Text is turned off in Settings, under Permissions, so the text selected in the browser was not used. " +
        "Turn Selected Text on there, then choose Ask Assistant in the browser again.";

    /// <summary>What the user is told when handing the selection over went wrong in a way that has no words of its own.</summary>
    internal const string FailedText = "The text selected in the browser couldn't be opened. Try choosing Ask Assistant again.";

    private readonly IPermissionGate _gate;
    private readonly AssistantWindowStateController _windows;
    private readonly ConversationViewModel _conversation;
    private readonly ILogger<AskBrowserSelectionController> _logger;

    /// <summary>Creates the controller.</summary>
    /// <param name="permissions">Says whether Selected Text is allowed.</param>
    /// <param name="windows">Opens the floating conversation.</param>
    /// <param name="conversation">The floating conversation that the text is attached to.</param>
    /// <param name="logger">Receives what happened, never what was selected or where.</param>
    /// <param name="gate">Asks the user before the selection is used when Selected Text is set to ask every time (step 119); without it, <paramref name="permissions"/> decides alone and such a selection is refused.</param>
    public AskBrowserSelectionController(
        IPermissionPolicy permissions,
        AssistantWindowStateController windows,
        ConversationViewModel conversation,
        ILogger<AskBrowserSelectionController> logger,
        IPermissionGate? gate = null)
    {
        _gate = gate ?? new PermissionGate(permissions, null, NullLogger<PermissionGate>.Instance);
        _windows = windows;
        _conversation = conversation;
        _logger = logger;
    }

    /// <summary>Opens the Ask panel with <paramref name="selection"/> attached and the keyboard in its composer. Called on the UI thread.</summary>
    /// <param name="selection">What the user selected in the browser.</param>
    /// <param name="browser">The browser's window as it was when the selection arrived, which the panel opens beside, or <see langword="null"/>.</param>
    public async Task OpenAsync(BrowserSelection selection, NearWindowTarget? browser = null)
    {
        ArgumentNullException.ThrowIfNull(selection);
        try
        {
            var grant = await _gate.RequestAsync(
                PermissionCapability.SelectedText, "You chose Ask Assistant in the browser. The selected text is held in memory and never saved.").ConfigureAwait(true);
            if (!grant.IsGranted)
            {
                BrowserBridgeLog.SelectionNotAllowed(_logger, grant.Decision.Reason);
                _conversation.StartWithNotice(
                    grant.Decision.Reason is PermissionDecisionReason.Declined or PermissionDecisionReason.CouldNotAsk
                        ? PermissionTexts.WhyNot(grant.Decision) + " The text selected in the browser was not used."
                        : TurnedOffText);
                Show(browser);
                return;
            }

            // The text arrived with the request and no service reads anything for it, so there is nothing for the yes to cover.

            // The page's text around the selection, when the user chose to share it, comes from a web page: cleaned again here, and bounded.
            var page = new WebPageOrigin(
                selection.PageTitle.Trim(), selection.PageUrl.Trim(), selection.BrowserName.Trim(),
                NearbyPageText.Clean(selection.NearbyBefore, BrowserSelection.MaxNearbyLength, NearbySide.Before),
                NearbyPageText.Clean(selection.NearbyAfter, BrowserSelection.MaxNearbyLength, NearbySide.After));
            _conversation.StartWithSelection(
                new TextAttachment(selection.Text, webPage: page), selection.IsTruncated ? AskSelectionController.TruncatedNotice : null);
            Show(browser);
            BrowserBridgeLog.SelectionOpened(_logger, page.HasNearbyContext);
        }
        catch (OperationCanceledException)
        {
            // The application is closing: nothing is left to tell.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failure here must not take the Assistant down: it is logged by type, and the user is told to try again.
            BrowserBridgeLog.SelectionFailed(_logger, exception.GetType().Name);
            _conversation.StartWithNotice(FailedText);
            Show(browser);
        }
    }

    // The panel opens and takes the keyboard. The browser is the window in front, and Windows may refuse another process's request to
    // bring a window forward, so the panel asks for it the way the application in front would.
    private void Show(NearWindowTarget? browser)
    {
        _windows.ShowConversationNear(browser);
        _windows.TakeForeground();
    }
}
