using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Core.Domain;
using Assistant.Core.MultiFile;
using Assistant.UI.Messages;

namespace Assistant.UI.Search;

/// <summary>What reading the documents attached to a question came to: the context to ask with, or why there is none.</summary>
/// <param name="Items">The documents' passages, or the notes on them, as context the model is given; empty when there is no document or a problem.</param>
/// <param name="Notices">What to tell the user about which documents were left out and how much was read, in the order to say it.</param>
/// <param name="Problem">What to tell the user when no document could be read, so nothing is asked; otherwise <see langword="null"/>.</param>
internal sealed record AttachedDocumentResult(IReadOnlyList<ContextItem> Items, IReadOnlyList<string> Notices, string? Problem)
{
    /// <summary>The result of a question with no document.</summary>
    public static AttachedDocumentResult None { get; } = new([], [], null);
}

/// <summary>
/// Reads the documents the user attached to a question, from their files, into context for the model, through the
/// <see cref="IMultiFileProcessor"/> (PROJECT_SPEC §5.5, several files): the passages of each that the question needs, each marked with
/// where it is, when they fit one prompt; otherwise the notes the local model takes on each piece of them, which the answer is put
/// together from. Reading a file needs the Files permission (PROJECT_SPEC §4.9): with it off, or with no policy to ask, nothing is read
/// and the user is told so. A document that is gone, cannot be opened, has no text to read, is protected by a password, is damaged,
/// too large or of a type nobody reads is said in words that name it: one such document alone means nothing is asked, since the answer
/// would be about something else, and among several it is left out and the question is asked about the rest (PROJECT_SPEC §4.4:
/// reported per file). The text lives in memory only, and neither it, the question nor a path is logged.
/// </summary>
internal sealed class AttachedDocuments
{
    /// <summary>What the user is told when the Files permission is off.</summary>
    public const string FilesTurnedOffText =
        "Files are turned off in Settings, under Permissions, so the attached file wasn't read and nothing was asked. Turn Files on there, then ask again.";

    /// <summary>What the user is told when there is no way to check the Files permission or to read a file.</summary>
    public const string NotAvailableText = "Attaching files isn't available here, so nothing was asked.";

    /// <summary>What the user is told first when none of several attached documents could be read.</summary>
    public const string NoneReadText = "None of the attached files could be read, so nothing was asked.";

    private readonly IPermissionPolicy? _permissions;
    private readonly IMultiFileProcessor? _processor;

    /// <summary>Creates the reader.</summary>
    /// <param name="permissions">Asks the Files permission, or <see langword="null"/> for no way to ask (nothing is read).</param>
    /// <param name="documents">Reads a file into passages, or <see langword="null"/> for no way to read one.</param>
    /// <param name="models">The local model, which the passages are sized for.</param>
    /// <param name="settings">The user's settings, for their context limits.</param>
    /// <param name="processor">
    /// Reads the documents of a question, taking notes when they do not fit one prompt; <see langword="null"/> for one over
    /// <paramref name="documents"/> that reads them directly, each to its share of one prompt, and takes no notes.
    /// </param>
    public AttachedDocuments(
        IPermissionPolicy? permissions,
        IDocumentContextService? documents,
        IModelService? models = null,
        ISettingsService? settings = null,
        IMultiFileProcessor? processor = null)
    {
        _permissions = permissions;
        _processor = processor
            ?? (documents is null ? null : new MultiFileProcessor(documents, models, settings, limits: new MultiFileLimits { TakeNotes = false }));
    }

    /// <summary>Reads <paramref name="document"/> for <paramref name="question"/> into context, or says why that cannot be done.</summary>
    public Task<AttachedDocumentResult> ReadAsync(DocumentAttachment? document, string question, CancellationToken cancellationToken) =>
        ReadAsync(document is null ? [] : [document], question, cancellationToken);

    /// <summary>
    /// Reads <paramref name="documents"/> for <paramref name="question"/> into context, or says why that cannot be done.
    /// </summary>
    /// <exception cref="OperationCanceledException">The reading was stopped.</exception>
    /// <exception cref="Core.ModelHosting.ModelHostException">The model could not take notes on the documents.</exception>
    public async Task<AttachedDocumentResult> ReadAsync(
        IReadOnlyList<DocumentAttachment> documents, string question, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(question);
        if (documents.Count == 0)
        {
            return AttachedDocumentResult.None;
        }

        if (_permissions is null || _processor is null)
        {
            return Problem(NotAvailableText);
        }

        if (!(await _permissions.CheckAsync(PermissionCapability.Files, cancellationToken).ConfigureAwait(true)).IsAllowed)
        {
            return Problem(FilesTurnedOffText);
        }

        var result = await _processor.ProcessAsync(
                [.. documents.Select(document => new QuestionFile(document.Name, document.Path))], question, cancellationToken)
            .ConfigureAwait(true);
        var unread = result.Files.Where(file => !file.IsRead).ToArray();
        if (!result.HasContext)
        {
            // One file that could not be read says why; several say that none could, and why each.
            return unread.Length == 1 && result.Files.Count == 1
                ? Problem(ProblemText(unread[0].File.Name, unread[0].Status))
                : Problem(string.Join(' ', [NoneReadText, .. unread.Select(file => Why(Quote(file.File.Name), file.Status) + ".")]));
        }

        return new AttachedDocumentResult(
            result.Items,
            [.. unread.Select(file => LeftOutText(file.File.Name, file.Status)), .. result.Notices],
            null);
    }

    /// <summary>What the user is told when a document gave no text: which it was, and what they can do about it.</summary>
    internal static string ProblemText(string name, DocumentReadStatus status)
    {
        var why = Why(Quote(name), status) + ", so nothing was asked.";
        return status == DocumentReadStatus.Unsupported ? $"{why} It reads {DocumentFileTypes.Described}." : why;
    }

    /// <summary>What the user is told of one of several documents that gave no text, when the question is asked about the rest.</summary>
    internal static string LeftOutText(string name, DocumentReadStatus status) => Why(Quote(name), status) + ", so it was left out.";

    // Why the file gave no text, in words that name it.
    private static string Why(string file, DocumentReadStatus status) => status switch
    {
        DocumentReadStatus.Unsupported => $"{file} isn't a kind of file the Assistant can read",
        DocumentReadStatus.NoText => $"{file} has no text the Assistant can read (a scanned document, say)",
        DocumentReadStatus.TooLarge => $"{file} is too large for the Assistant to read",
        DocumentReadStatus.Encrypted => $"{file} is protected by a password, which the Assistant can't use",
        DocumentReadStatus.Corrupt => $"{file} looks damaged, or isn't the kind of file its name says",
        DocumentReadStatus.NotFound => $"{file} can't be found any more. It may have been moved or deleted",
        DocumentReadStatus.NotAllowed => $"Files is turned off in Settings, under Permissions, so {file} wasn't read",
        _ => $"{file} couldn't be opened. Another program may be using it, or you may not have access to it",
    };

    private static AttachedDocumentResult Problem(string text) => new([], [], text);

    // The name in quotes, on one line and not long: it is the user's own file name, and only ever shown to them.
    private static string Quote(string name)
    {
        var clean = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (clean.Length == 0)
        {
            return "The attached file";
        }

        return clean.Length <= 60 ? $"“{clean}”" : $"“{clean[..59].TrimEnd()}…”";
    }
}
