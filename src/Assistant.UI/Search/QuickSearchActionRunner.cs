using System.IO;
using Assistant.Core.Contracts;
using Assistant.Core.QuickSearch;
using Assistant.Core.QuickSearch.Actions;
using Assistant.Core.QuickSearch.Clipboard;
using Assistant.UI.Messages;

namespace Assistant.UI.Search;

/// <summary>
/// Runs what the user chose on a quick-search result (PROJECT_SPEC §4.1): Open, Show in File Explorer, Attach to conversation, Copy path,
/// or one of the Assistant's own actions. Every one is deterministic and happens outside the model; each is handed to the one thing that
/// does it (the shell, the clipboard, the conversation, the action executor), and the runner decides only whether the bar goes away
/// afterwards. A result that was opened, shown or run counts as used, so what the user runs comes first next time. Targets and arguments
/// are paths, names and copied text, which are private content: nothing here logs them.
/// </summary>
internal sealed class QuickSearchActionRunner(
    IApplicationLauncher applications, IFileLauncher files, ITextClipboard clipboard, AttachRequests attachments,
    IQuickActionExecutor actions, IClipboardHistory? history = null, IQuickSearchUsage? usage = null)
{
    /// <summary>Raised on the UI thread when a result was run and the bar should go.</summary>
    public event EventHandler? Finished;

    /// <summary>Raised on the UI thread, with a sentence for the user, when what was chosen could not be done; the bar stays.</summary>
    public event EventHandler<string>? Failed;

    /// <summary>Raised on the UI thread when what is listed has changed under the bar (an item was taken out of the history).</summary>
    public event EventHandler? ListChanged;

    // The actions that deal with the bar themselves: attaching grows it into the conversation, and the others have their own way of leaving.
    private static readonly HashSet<string> LeavesTheBarAlone = new(StringComparer.Ordinal)
    {
        QuickActionIds.NewConversation, QuickActionIds.TakeScreenshot,
    };

    /// <summary>
    /// Runs <paramref name="action"/> on <paramref name="result"/>. An action that takes time (one of the Assistant's own) is awaited
    /// without holding anything up; the outcome is reported through <see cref="Finished"/> or <see cref="Failed"/>.
    /// </summary>
    public void Run(QuickSearchResult result, QuickSearchAction action)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(action);
        _ = RunAsync(result, action);
    }

    /// <summary>As <see cref="Run"/>, for a caller that wants to wait for it.</summary>
    public async Task RunAsync(QuickSearchResult result, QuickSearchAction action)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(action);

        Outcome outcome;
        try
        {
            outcome = await ExecuteAsync(action).ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whatever went wrong is a failure to do what was chosen; what it said can hold a path, so only the fact is passed on.
            outcome = Outcome.Fail("That could not be done.");
        }

        if (!outcome.Succeeded)
        {
            Failed?.Invoke(this, outcome.Message ?? "That could not be done.");
            return;
        }

        // What was run is used: copied text and what is taken out of the history are not something to find again by its name.
        if (result.ResultType != QuickSearchResultType.Clipboard)
        {
            usage?.RecordUse(result.Id);
        }

        if (outcome.ListChanged)
        {
            ListChanged?.Invoke(this, EventArgs.Empty);
        }

        if (outcome.CloseBar)
        {
            Finished?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<Outcome> ExecuteAsync(QuickSearchAction action)
    {
        switch (action.Kind)
        {
            case QuickSearchActionKind.LaunchApplication:
                return Of(applications.Launch(action.Target), "That application could not be opened.");
            case QuickSearchActionKind.OpenPath:
                return Of(files.Open(action.Target), "That could not be opened. It may have been moved or deleted.");
            case QuickSearchActionKind.RevealPath:
                return Of(files.Reveal(action.Target), "That could not be shown. It may have been moved or deleted.");
            case QuickSearchActionKind.CopyPath:
                return Of(clipboard.TrySetText(action.Target), "The clipboard is busy. Try again.");
            case QuickSearchActionKind.CopyFile:
                // The file itself, as File Explorer copies it: one that has gone since it was listed is not put on the clipboard as a name with nothing behind it.
                return File.Exists(action.Target) || Directory.Exists(action.Target)
                    ? Of(clipboard.TrySetFiles([action.Target]), "The clipboard is busy. Try again.")
                    : Outcome.Fail("That could not be copied. It may have been moved or deleted.");
            case QuickSearchActionKind.AttachFile:
                return Attach(action.Target);
            case QuickSearchActionKind.RunAction:
            {
                var outcome = await actions.RunAsync(action.Target, action.Argument).ConfigureAwait(true);
                return outcome.Succeeded
                    ? Outcome.Done(closeBar: !LeavesTheBarAlone.Contains(action.Target))
                    : Outcome.Fail(outcome.Message ?? "That could not be done.");
            }

            case QuickSearchActionKind.CopyClipboardItem:
                return history?.Find(action.Target) is { } copied
                    ? Of(clipboard.TrySetText(copied.Text), "The clipboard is busy. Try again.")
                    : Outcome.Fail("That is no longer in the clipboard history.");
            case QuickSearchActionKind.AttachClipboardItem:
                if (history?.Find(action.Target) is not { } attached)
                {
                    return Outcome.Fail("That is no longer in the clipboard history.");
                }

                attachments.Request(new TextAttachment(attached.Text));
                return Outcome.Done(closeBar: false);
            case QuickSearchActionKind.RemoveClipboardItem:
                return history?.Remove(action.Target) == true
                    ? new Outcome(true, null, CloseBar: false, ListChanged: true)
                    : Outcome.Fail("That is no longer in the clipboard history.");
            default:
                return Outcome.Fail("That cannot be done.");
        }
    }

    // A picture, or a document the Assistant reads, is attached to a new conversation, which the bar grows into.
    private Outcome Attach(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name))
        {
            return Outcome.Fail("That cannot be attached.");
        }

        var extension = Path.GetExtension(path);
        if (ImageFileTypes.IsImageExtension(extension))
        {
            attachments.Request(new ImageItem(name, path));
        }
        else if (DocumentFileTypes.IsDocumentExtension(extension))
        {
            attachments.Request(new DocumentAttachment(name, path));
        }
        else
        {
            return Outcome.Fail("The Assistant cannot read that kind of file.");
        }

        return Outcome.Done(closeBar: false);
    }

    private static Outcome Of(bool succeeded, string failure) => succeeded ? Outcome.Done() : Outcome.Fail(failure);

    private sealed record Outcome(bool Succeeded, string? Message, bool CloseBar = true, bool ListChanged = false)
    {
        public static Outcome Done(bool closeBar = true) => new(true, null, closeBar);

        public static Outcome Fail(string message) => new(false, message);
    }
}
