using Assistant.Core.Domain;
using Assistant.Core.Files;

namespace Assistant.Core.Contracts;

/// <summary>
/// The files a conversation has made known to the model: the ones a search found for it and the ones the user attached to it
/// (PROJECT_SPEC §4.8). Each gets an id within the conversation (<c>f1</c>, <c>f2</c>, ...) that the model passes to the tool that
/// reads a file, which is how "the first one" or "the other one" comes to mean a file: the model reads the list in the
/// conversation and names the id. A file that was never offered cannot be read, whatever the model asks for.
/// </summary>
/// <remarks>
/// It holds paths in memory only, for the conversations touched last, and never saves or logs them. It does not read or search
/// anything: whoever found or was given a file offers it.
/// </remarks>
public interface IConversationFiles
{
    /// <summary>
    /// Makes <paramref name="items"/> known to the conversation, and returns them with their ids, in the order given. A file that
    /// is already known keeps its id.
    /// </summary>
    IReadOnlyList<KnownFile> Offer(Guid conversationId, IEnumerable<SearchResultItem> items);

    /// <summary>
    /// The known file that <paramref name="reference"/> names: its id, or else its name (with or without the extension, or a part
    /// of it that only one file has), or else its path. <see langword="null"/> when no known file is meant.
    /// </summary>
    KnownFile? Find(Guid conversationId, string reference);

    /// <summary>The known files with the given ids, in the order given; an id that is not known is skipped.</summary>
    IReadOnlyList<KnownFile> Get(Guid conversationId, IEnumerable<string> ids);

    /// <summary>Whether the conversation has any file known to it.</summary>
    bool Has(Guid conversationId);

    /// <summary>Forgets the files of the conversation.</summary>
    void Forget(Guid conversationId);
}
