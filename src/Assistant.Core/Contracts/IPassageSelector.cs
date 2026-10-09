using Assistant.Core.Documents;

namespace Assistant.Core.Contracts;

/// <summary>
/// Chooses which passages of a document go into a prompt for a question (PROJECT_SPEC §4.7), so the model reads what the
/// question needs and not a whole document. v0.1 ranks by the words of the question (<c>LexicalPassageSelector</c>); the seam
/// is here so another way of ranking can replace it without the rest changing.
/// </summary>
public interface IPassageSelector
{
    /// <summary>Selects the passages of <paramref name="passages"/> that best answer <paramref name="question"/> within the limits.</summary>
    /// <param name="passages">The passages of one document, in the order of the document.</param>
    /// <param name="question">What the user asked. Never logged.</param>
    /// <param name="options">The limits, or <see langword="null"/> for <see cref="PassageSelectionOptions.Default"/>.</param>
    /// <returns>The selected passages in the order of the document. The same arguments always give the same selection.</returns>
    PassageSelection Select(IReadOnlyList<DocumentPassage> passages, string question, PassageSelectionOptions? options = null);
}
