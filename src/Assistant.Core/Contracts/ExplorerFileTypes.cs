namespace Assistant.Core.Contracts;

/// <summary>
/// The kinds of file File Explorer offers Ask Assistant for (PROJECT_SPEC §4.4): every document the Assistant reads
/// (<see cref="DocumentFileTypes"/>) and every picture it shows (<see cref="ImageFileTypes"/>), which are what a conversation
/// can attach. The menu entry is registered for these extensions, and the app takes nothing else from File Explorer.
/// </summary>
public static class ExplorerFileTypes
{
    /// <summary>The extensions, lower case with their dot, documents first, each once.</summary>
    public static IReadOnlyList<string> Extensions { get; } =
        [.. DocumentFileTypes.Extensions.Concat(ImageFileTypes.Extensions).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>Whether the file at <paramref name="path"/> has one of the <see cref="Extensions"/>. The file is not opened.</summary>
    public static bool IsSupported(string? path) =>
        !string.IsNullOrWhiteSpace(path)
        && (DocumentFileTypes.IsDocument(path) || ImageFileTypes.IsImageExtension(Path.GetExtension(path)));
}
