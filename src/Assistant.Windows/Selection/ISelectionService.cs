namespace Assistant.Windows.Selection;

/// <summary>
/// Reads the text selected in the application in the foreground. It is asked only in direct response to something the user did
/// (PROJECT_SPEC §3.1, P2), and never changes the selection, the focus or the clipboard.
/// </summary>
public interface ISelectionService
{
    /// <summary>
    /// Asks the foreground application for its selected text and says which application that is. It does not throw for an
    /// application that does not expose its selection: the <see cref="SelectionResult.Status"/> says what happened instead.
    /// Only cancellation throws.
    /// </summary>
    Task<SelectionResult> GetSelectionAsync(CancellationToken cancellationToken = default);
}
