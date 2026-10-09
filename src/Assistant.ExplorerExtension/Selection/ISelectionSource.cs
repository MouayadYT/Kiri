namespace Assistant.ExplorerExtension.Selection;

/// <summary>Where the files selected in File Explorer are read from, when the entry point was started for one of them.</summary>
internal interface ISelectionSource
{
    /// <summary>
    /// The paths selected in the File Explorer window that has <paramref name="clicked"/> selected, in the order the window lists
    /// them, or <see langword="null"/> when no window can be found that has them all selected (the window was closed, the files are
    /// not in one, or Explorer did not answer).
    /// </summary>
    IReadOnlyList<string>? TryRead(IReadOnlyList<string> clicked);
}
