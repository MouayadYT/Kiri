using System.Text;
using Assistant.Core.Domain;

namespace Assistant.Core.Files;

/// <summary>A file made known to a conversation, with the id the model names it by.</summary>
/// <param name="Id">The id within the conversation: <c>f1</c>, <c>f2</c>, ...</param>
/// <param name="Item">The file or folder as the search or the attachment gave it.</param>
public sealed record KnownFile(string Id, SearchResultItem Item)
{
    /// <summary>The file's full path.</summary>
    public string Path => Item.Path;

    /// <summary>What the file is called, with its extension.</summary>
    public string Name => System.IO.Path.GetFileName(Item.Path) is { Length: > 0 } name ? name : Item.DisplayName;

    /// <summary>The name of the folder the file is in, or <see langword="null"/> when its path says none.</summary>
    public string? Folder =>
        System.IO.Path.GetDirectoryName(Item.Path) is { Length: > 0 } directory
            && System.IO.Path.GetFileName(directory) is { Length: > 0 } folder
                ? folder
                : null;

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Id = {Id}, Type = {Item.Type}");
        return true;
    }
}
