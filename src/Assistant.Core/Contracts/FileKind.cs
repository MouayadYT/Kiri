namespace Assistant.Core.Contracts;

/// <summary>
/// A broad kind of file, as Windows itself classifies it (the <c>System.Kind</c> property of the index). It is only as good as
/// Windows' own knowledge of the file type: a type it has no kind for (a <c>.md</c> or a <c>.log</c> file) is found by its
/// extension instead.
/// </summary>
public enum FileKind
{
    /// <summary>Documents: text, Office and PDF files and the like.</summary>
    Document = 0,

    /// <summary>Pictures, and so screenshots.</summary>
    Picture = 1,

    /// <summary>Videos.</summary>
    Video = 2,

    /// <summary>Music and other audio.</summary>
    Music = 3,
}
