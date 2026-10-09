namespace Assistant.Windows.Selection;

/// <summary>
/// The fallback for applications that do not expose their selection to UI Automation (PROJECT_SPEC §4.5, capture order 2): it presses Copy
/// (Ctrl+C) in the application in front, reads what it put on the clipboard, and puts the user's previous clipboard back. Unlike
/// <see cref="ISelectionService"/> it acts on the other application and on the clipboard, so a caller runs it only because the user asked
/// for it by name, never as a silent second attempt, and only while the user has allowed it.
/// </summary>
public interface ICopySelectionService
{
    /// <summary>
    /// Copies the selection of the application in front and reads it. It does not throw for anything that goes wrong with the application or
    /// the clipboard: the <see cref="CopySelectionResult.Status"/> says what happened. Only cancellation, before Copy is pressed, throws.
    /// </summary>
    Task<CopySelectionResult> CopySelectionAsync(CancellationToken cancellationToken = default);
}
