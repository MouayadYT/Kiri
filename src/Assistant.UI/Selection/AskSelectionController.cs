using System.Globalization;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Selection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UI.Selection;

/// <summary>
/// Ask Selection (PROJECT_SPEC §4.5): what happens when the selected-text shortcut is pressed. The text selected in the application in
/// front is read, before the Assistant takes the keyboard, and the floating Ask panel opens as a new conversation with that text
/// attached, the quick actions listed above the composer and the keyboard in it. Nothing is asked: the user picks an action, which
/// only writes its request in the composer, or types a question of their own, and sends it.
/// </summary>
/// <remarks>
/// <para>
/// It only reads when the user presses the shortcut, and only while the Selected Text permission is on. When there is nothing to read
/// (no selection, an application that does not share it, a locked PC) the panel says so in words; it never guesses a selection from
/// the screen or the clipboard. The text stays in memory, on the conversation's chip, until the user takes it off or another
/// conversation replaces it; it is never saved, and neither it nor a window title is logged.
/// </para>
/// <para>The shortcut does nothing while the Assistant's own window is the one in front, which has no other application's text to read.</para>
/// </remarks>
internal sealed class AskSelectionController
{
    /// <summary>What the user is told when Selected Text is turned off in the Settings.</summary>
    internal const string TurnedOffText =
        "Selected Text is turned off in Settings, under Permissions, so nothing was read. Turn Selected Text on there, then use the shortcut again.";

    /// <summary>What the user is told when the application in front has no text selected.</summary>
    internal const string NoSelectionText =
        "No text is selected. Select some text in the app you're working in, then use the shortcut again.";

    /// <summary>What the user is told when the application in front does not share its selection.</summary>
    internal const string UnsupportedText =
        "This app doesn't let the Assistant read its selected text, so nothing was captured.";

    /// <summary>What the user is told when there is no application in front to ask.</summary>
    internal const string NoForegroundText =
        "The selected text can't be read while the PC is locked or a secure prompt is showing.";

    /// <summary>What the user is told when reading the selection went wrong in a way that has no words of its own.</summary>
    internal const string FailedText = "The selected text couldn't be read. Try the shortcut again.";

    /// <summary>What the user is told, above the composer, when only the start of a very long selection was read.</summary>
    internal static readonly string TruncatedNotice = string.Create(
        CultureInfo.InvariantCulture,
        $"The selection is very long, so only its first {SelectionService.MaxTextLength:N0} characters were read.");

    private readonly ISelectionService _selection;
    private readonly IPermissionGate _gate;
    private readonly AssistantWindowStateController _windows;
    private readonly ConversationViewModel _conversation;
    private readonly ILogger<AskSelectionController> _logger;
    private readonly ICopySelectionService? _copy;
    private readonly ISettingsService? _settings;
    private readonly int _ownProcessId;
    private bool _running;

    /// <summary>Creates the controller.</summary>
    /// <param name="selection">Reads the selected text of the application in front.</param>
    /// <param name="permissions">Says whether Selected Text is allowed.</param>
    /// <param name="windows">Opens the floating conversation.</param>
    /// <param name="conversation">The floating conversation that the text is attached to.</param>
    /// <param name="logger">Receives what happened, never what was selected.</param>
    /// <param name="ownProcessId">The id of this application's process, or <see langword="null"/> for the running one.</param>
    /// <param name="copy">The copy fallback (step 89), or <see langword="null"/> for none; only <see cref="InvokeByCopyAsync"/> uses it.</param>
    /// <param name="settings">Where the copy shortcut is read from, to tell the user about it, or <see langword="null"/> to say nothing about it.</param>
    /// <param name="gate">Asks the user before a selection is read when Selected Text (or Selected Text by Copy) is set to ask every time (step 119); without it, <paramref name="permissions"/> decides alone and such a read is refused.</param>
    public AskSelectionController(
        ISelectionService selection,
        IPermissionPolicy permissions,
        AssistantWindowStateController windows,
        ConversationViewModel conversation,
        ILogger<AskSelectionController> logger,
        int? ownProcessId = null,
        ICopySelectionService? copy = null,
        ISettingsService? settings = null,
        IPermissionGate? gate = null)
    {
        _copy = copy;
        _settings = settings;
        _selection = selection;
        _gate = gate ?? new PermissionGate(permissions, null, NullLogger<PermissionGate>.Instance);
        _windows = windows;
        _conversation = conversation;
        _logger = logger;
        _ownProcessId = ownProcessId ?? Environment.ProcessId;
    }

    /// <summary>What the user is told when Selected Text by Copy is turned off in the Settings.</summary>
    internal const string CopyTurnedOffText =
        "Selected Text by Copy is turned off in Settings, under Permissions, so nothing was copied. Turn it on there if you want the Assistant " +
        "to press Copy in apps that don't share their selection.";

    /// <summary>What the user is told when the app did not put a copy on the clipboard.</summary>
    internal const string NothingCopiedText =
        "Nothing was copied. Select some text in the app you're working in, then use the copy shortcut again. Some apps ignore a Copy " +
        "that another program sends.";

    /// <summary>What the user is told when the app copied something that is not text.</summary>
    internal const string NotTextText = "The app copied something that isn't text, so there is nothing to ask about.";

    /// <summary>What the user is told when the app in front is a terminal.</summary>
    internal const string UnsafeAppText =
        "The Assistant doesn't press Copy in a terminal, because Ctrl+C there can stop the program that is running. Nothing was sent.";

    /// <summary>What the user is told when the selected field is a password box.</summary>
    internal const string ProtectedControlText = "The field you're in is a password box, so nothing was copied.";

    /// <summary>What the user is told when the clipboard holds something that cannot be put back exactly.</summary>
    internal const string ClipboardNotSavedText =
        "Your clipboard holds something the Assistant can't put back exactly (such as a file or an object from another app), so it didn't " +
        "press Copy and nothing was changed. Paste or clear the clipboard, then use the copy shortcut again.";

    /// <summary>What the user is told when the clipboard was in use by another app.</summary>
    internal const string ClipboardBusyText = "The clipboard is in use by another app, so the selection couldn't be copied. Try again.";

    /// <summary>What the user is told when they were still holding the shortcut's keys.</summary>
    internal const string KeysHeldText =
        "The shortcut's keys were still held down, so nothing was copied. Press the shortcut once and let go.";

    /// <summary>What the user is told when the window in front changed before Copy was pressed.</summary>
    internal const string ForegroundChangedText = "The window in front changed, so nothing was copied. Try again.";

    /// <summary>What the user is told, above the composer, when text was copied and their previous clipboard is back.</summary>
    internal const string CopiedRestoredNotice = "Copied from the app with Ctrl+C. Your previous clipboard was put back.";

    /// <summary>What the user is told, above the composer, when text was copied but the clipboard had changed meanwhile.</summary>
    internal const string CopiedChangedNotice =
        "Copied from the app with Ctrl+C. The clipboard changed in the meantime, so your previous clipboard was not put back over it.";

    /// <summary>What the user is told, above the composer, when text was copied and the previous clipboard could not be put back.</summary>
    internal const string CopiedNotRestoredNotice =
        "Copied from the app with Ctrl+C, but your previous clipboard couldn't be put back: the clipboard now holds the copied text.";

    /// <summary>What is added to a message when the previous clipboard could not be put back after Copy was pressed.</summary>
    internal const string NotRestoredSentence = " Your previous clipboard couldn't be put back.";

    /// <summary>What is added to a message when the clipboard changed after Copy was pressed, so the previous one was left alone.</summary>
    internal const string ChangedSentence = " The clipboard changed in the meantime, so your previous clipboard was left alone.";

    /// <summary>Whether a selection is being read.</summary>
    public bool IsRunning => _running;

    /// <summary>The shortcut, or a command: reads the selected text and opens the Ask panel with it. Pressed again while it is reading, it does nothing.</summary>
    public async Task InvokeAsync()
    {
        if (_running)
        {
            return;
        }

        _running = true;
        try
        {
            AskSelectionLog.Started(_logger);
            var grant = await _gate.RequestAsync(
                PermissionCapability.SelectedText, "You used the selected-text shortcut. The text selected in the app in front is read, held in memory and never saved.")
                .ConfigureAwait(true);
            if (!grant.IsGranted)
            {
                AskSelectionLog.NotAllowed(_logger, grant.Decision.Reason);
                Tell(RefusalText(grant.Decision, TurnedOffText));
                return;
            }

            // The Assistant has not taken the keyboard yet, so the application the user was in is still the one in front. The user's yes (when the permission asks each time)
            // is for this read and nothing after it.
            SelectionResult result;
            using (grant.Enter())
            {
                result = await _selection.GetSelectionAsync().ConfigureAwait(true);
            }

            AskSelectionLog.Read(_logger, result.Status, result.App?.ProcessName ?? string.Empty, result.Text?.Length ?? 0, result.IsTruncated);
            if (result.App?.ProcessId == _ownProcessId)
            {
                AskSelectionLog.OwnWindow(_logger);
                return;
            }

            switch (result.Status)
            {
                case SelectionStatus.Selected when !string.IsNullOrWhiteSpace(result.Text):
                    _conversation.StartWithSelection(new TextAttachment(result.Text), result.IsTruncated ? TruncatedNotice : null);
                    Show();
                    AskSelectionLog.Opened(_logger);
                    break;
                case SelectionStatus.Unsupported:
                    Tell(await UnsupportedWithHintAsync().ConfigureAwait(true));
                    break;
                case SelectionStatus.NoForegroundApp:
                    Tell(NoForegroundText);
                    break;
                default:
                    Tell(NoSelectionText);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // The application is closing: nothing is left to tell.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A failure here must not take the Assistant down: it is logged by type, and the user is told to try again.
            AskSelectionLog.Failed(_logger, exception.GetType().Name);
            Tell(FailedText);
        }
        finally
        {
            _running = false;
        }
    }

    /// <summary>
    /// The copy shortcut (step 89): for an app that does not share its selection, presses Copy in the app in front, reads what it copied and puts
    /// the user's clipboard back, then opens the Ask panel with the text and a notice that says Copy was used and what became of the clipboard.
    /// It runs only because the user pressed this shortcut by name, and only while Selected Text and Selected Text by Copy are both on; the
    /// ordinary shortcut never falls back to it by itself. Pressed again while it is working, it does nothing.
    /// </summary>
    public async Task InvokeByCopyAsync()
    {
        if (_running || _copy is null)
        {
            return;
        }

        _running = true;
        try
        {
            AskSelectionLog.CopyStarted(_logger);
            var selected = await _gate.RequestAsync(
                PermissionCapability.SelectedText, "You used the copy shortcut. The text selected in the app in front is read, held in memory and never saved.")
                .ConfigureAwait(true);
            if (!selected.IsGranted)
            {
                AskSelectionLog.NotAllowed(_logger, selected.Decision.Reason);
                Tell(RefusalText(selected.Decision, TurnedOffText));
                return;
            }

            var byCopy = await _gate.RequestAsync(
                PermissionCapability.SelectedTextByCopy, "You used the copy shortcut. The Assistant presses Copy (Ctrl+C) in the app in front, reads what it copied and puts your clipboard back.")
                .ConfigureAwait(true);
            if (!byCopy.IsGranted)
            {
                AskSelectionLog.CopyNotAllowed(_logger, byCopy.Decision.Reason);
                Tell(RefusalText(byCopy.Decision, CopyTurnedOffText));
                return;
            }

            // The Assistant has not taken the keyboard yet, so the application the user was in is still the one Copy is pressed in.
            CopySelectionResult result;
            using (selected.Enter())
            using (byCopy.Enter())
            {
                result = await _copy.CopySelectionAsync().ConfigureAwait(true);
            }

            AskSelectionLog.Copied(
                _logger, result.Status, result.Restore, result.App?.ProcessName ?? string.Empty, result.Text?.Length ?? 0, result.IsTruncated);
            if (result.Status == CopySelectionStatus.OwnWindow || result.App?.ProcessId == _ownProcessId)
            {
                AskSelectionLog.OwnWindow(_logger);
                return;
            }

            if (result.Status == CopySelectionStatus.Copied && !string.IsNullOrWhiteSpace(result.Text))
            {
                var notice = CopiedNoticeFor(result.Restore);
                _conversation.StartWithSelection(
                    new TextAttachment(result.Text), result.IsTruncated ? notice + " " + TruncatedNotice : notice);
                Show();
                AskSelectionLog.Opened(_logger);
                return;
            }

            Tell(CopyFailureText(result));
        }
        catch (OperationCanceledException)
        {
            // The application is closing: nothing is left to tell.
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            AskSelectionLog.Failed(_logger, exception.GetType().Name);
            Tell(FailedText);
        }
        finally
        {
            _running = false;
        }
    }

    // Why nothing was read: the user did not allow this use when asked (said in the permission's own words), or the permission is off (its own text).
    private static string RefusalText(PermissionDecision decision, string turnedOffText) =>
        decision.Reason is PermissionDecisionReason.Declined or PermissionDecisionReason.CouldNotAsk
            ? PermissionTexts.WhyNot(decision) + " Nothing was read."
            : turnedOffText;

    // What the user is told when the app in front does not share its selection: what to do about it when the copy shortcut exists, so the
    // fallback is something they reach for by name and never something that happens to them.
    private async Task<string> UnsupportedWithHintAsync()
    {
        if (_copy is null || _settings is null)
        {
            return UnsupportedText;
        }

        var settings = await _settings.LoadAsync().ConfigureAwait(true);
        if (!settings.Permissions.SelectedTextByCopy)
        {
            return UnsupportedText + " To try another way, turn on Selected Text by Copy in Settings, under Permissions.";
        }

        return settings.Hotkeys.SelectedTextByCopy is { } shortcut
            ? $"{UnsupportedText} Press {shortcut} to try copying the selection instead: it presses Copy in the app and puts your clipboard back."
            : UnsupportedText;
    }

    private static string CopiedNoticeFor(ClipboardRestoreOutcome restore) => restore switch
    {
        ClipboardRestoreOutcome.ChangedByOther => CopiedChangedNotice,
        ClipboardRestoreOutcome.Failed => CopiedNotRestoredNotice,
        _ => CopiedRestoredNotice,
    };

    // The words for a copy that gave no text, with a sentence about the clipboard when Copy was pressed and it could not be put back whole.
    private static string CopyFailureText(CopySelectionResult result)
    {
        var text = result.Status switch
        {
            CopySelectionStatus.NothingCopied => NothingCopiedText,
            CopySelectionStatus.NotText => NotTextText,
            CopySelectionStatus.UnsafeApp => UnsafeAppText,
            CopySelectionStatus.ProtectedControl => ProtectedControlText,
            CopySelectionStatus.ClipboardNotSaved => ClipboardNotSavedText,
            CopySelectionStatus.ClipboardBusy => ClipboardBusyText,
            CopySelectionStatus.KeysHeld => KeysHeldText,
            CopySelectionStatus.ForegroundChanged => ForegroundChangedText,
            CopySelectionStatus.NoForegroundApp => NoForegroundText,
            _ => FailedText,
        };
        return result.Restore switch
        {
            ClipboardRestoreOutcome.Failed => text + NotRestoredSentence,
            ClipboardRestoreOutcome.ChangedByOther => text + ChangedSentence,
            _ => text,
        };
    }

    // Says why nothing was captured, in the floating conversation, where the user can still ask a question of their own.
    private void Tell(string text)
    {
        _conversation.StartWithNotice(text);
        Show();
    }

    // The panel opens and takes the keyboard. Reading the selection took a moment, in which Windows may have taken back the right to bring
    // a window forward, so the panel asks for it the way the application in front would.
    private void Show()
    {
        _windows.ShowConversation();
        _windows.TakeForeground();
    }
}
