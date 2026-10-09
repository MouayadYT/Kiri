namespace Assistant.Core.QuickSearch;

/// <summary>
/// The type of a quick-search result, which is also the group it is listed in under the Search or Ask bar (PROJECT_SPEC §4.1).
/// A provider returns results of one type. A later step that adds a kind of result (contacts, messages, definitions) adds its
/// member at the end, so values already written down keep their meaning, and gives it a place in <see cref="QuickSearchGroups"/>.
/// </summary>
public enum QuickSearchResultType
{
    /// <summary>Installed and Start menu applications, packaged ones included.</summary>
    Applications = 0,

    /// <summary>Files and folders found in the Windows Search index.</summary>
    Files = 1,

    /// <summary>Things the Assistant can do on the user's behalf: open settings, lock the PC, change the volume.</summary>
    Actions = 2,

    /// <summary>Text the user copied, from the clipboard history the Assistant keeps once the user has allowed it.</summary>
    Clipboard = 3,
}
