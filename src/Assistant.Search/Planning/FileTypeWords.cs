using Assistant.Core.Contracts;

namespace Assistant.Search.Planning;

/// <summary>A kind of file a request can name, the extensions it stands for, and what the answer calls it.</summary>
/// <param name="Singular">The answer's word for one: "PDF", "document".</param>
/// <param name="Plural">The answer's word for several: "PDFs", "documents".</param>
/// <param name="Extensions">The extensions it stands for, with their dots.</param>
/// <param name="IsScreenshot">Whether it is a screenshot: an image whose name says so.</param>
internal sealed record FileType(string Singular, string Plural, IReadOnlyList<string> Extensions, bool IsScreenshot = false)
{
    /// <summary>Whether every file of this kind is a picture the gallery can show.</summary>
    public bool IsImages => Extensions.All(ImageFileTypes.IsImageExtension);
}

/// <summary>
/// The words that name kinds of files ("pdf", "doc", "spreadsheet", "screenshots", "mp4"), read with room for a typing mistake
/// in the long ones ("documnet", "spreadhseet") and a doubled last letter in the shortest ("pdff"). Other short words must be
/// typed as they are, since a mistake in one makes another word ("doc" and "dog", "sheet" and "sweet").
/// </summary>
internal static class FileTypeWords
{
    // A word shorter than this is one mistake from too many others ("sheet" and "sweet", "slides" and "slices").
    private const int MinMistypedLength = 7;

    private static readonly string[] OfficeDocuments = [".docx", ".doc", ".pdf", ".odt", ".rtf"];

    public static readonly FileType Pdf = new("PDF", "PDFs", [".pdf"]);
    public static readonly FileType Document = new("document", "documents", OfficeDocuments);
    public static readonly FileType WordDocument = new("Word document", "Word documents", [".docx", ".doc"]);
    public static readonly FileType Spreadsheet = new("spreadsheet", "spreadsheets", [".xlsx", ".xls", ".csv", ".ods"]);
    public static readonly FileType Presentation = new("presentation", "presentations", [".pptx", ".ppt", ".odp", ".key"]);
    public static readonly FileType Image = new("image", "images", ImageFileTypes.Extensions);
    public static readonly FileType Screenshot = new("screenshot", "screenshots", ImageFileTypes.Extensions, IsScreenshot: true);
    public static readonly FileType Video = new("video", "videos", [".mp4", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".m4v"]);
    public static readonly FileType Audio = new("audio file", "audio files", [".mp3", ".wav", ".flac", ".m4a", ".aac", ".ogg", ".wma"]);
    public static readonly FileType Compressed = new("zip file", "zip files", [".zip", ".rar", ".7z"]);
    public static readonly FileType Text = new("text file", "text files", [".txt", ".md"]);

    // Word, whether it says several, and the kind. Longer words are also read when mistyped.
    private static readonly (string Word, bool Plural, FileType Type)[] Words =
    [
        ("pdf", false, Pdf), ("pdfs", true, Pdf),
        ("doc", false, Document), ("docs", true, Document), ("document", false, Document), ("documents", true, Document),
        ("docx", false, WordDocument),
        ("spreadsheet", false, Spreadsheet), ("spreadsheets", true, Spreadsheet), ("excel", false, Spreadsheet),
        ("workbook", false, Spreadsheet), ("workbooks", true, Spreadsheet), ("sheet", false, Spreadsheet), ("sheets", true, Spreadsheet),
        ("presentation", false, Presentation), ("presentations", true, Presentation), ("powerpoint", false, Presentation),
        ("powerpoints", true, Presentation), ("slides", true, Presentation), ("slideshow", false, Presentation),
        ("deck", false, Presentation), ("decks", true, Presentation),
        ("image", false, Image), ("images", true, Image), ("photo", false, Image), ("photos", true, Image),
        ("picture", false, Image), ("pictures", true, Image), ("pic", false, Image), ("pics", true, Image),
        ("screenshot", false, Screenshot), ("screenshots", true, Screenshot),
        ("video", false, Video), ("videos", true, Video), ("movie", false, Video), ("movies", true, Video),
        ("clip", false, Video), ("clips", true, Video),
        ("song", false, Audio), ("songs", true, Audio), ("music", true, Audio), ("audio", false, Audio), ("track", false, Audio),
        ("tracks", true, Audio), ("recording", false, Audio), ("recordings", true, Audio),
        ("zip", false, Compressed), ("zips", true, Compressed), ("rar", false, Compressed),
        ("txt", false, Text),
    ];

    // Extensions that, as a word of their own, mean files of exactly that type.
    private static readonly HashSet<string> Extensions = new(StringComparer.Ordinal)
    {
        "docx", "xlsx", "xls", "csv", "pptx", "ppt", "odt", "rtf", "png", "jpg", "jpeg", "gif", "bmp", "webp", "heic", "tif",
        "tiff", "mp3", "wav", "flac", "m4a", "mp4", "mov", "mkv", "avi", "json", "xml", "html", "md", "7z", "iso", "exe", "msi",
    };

    /// <summary>
    /// The kind of file <paramref name="word"/> names, and whether it names several, or <see langword="null"/> when it names none.
    /// </summary>
    public static (FileType Type, bool Plural)? Read(string word)
    {
        ArgumentNullException.ThrowIfNull(word);
        foreach (var (name, plural, type) in Words)
        {
            if (word == name)
            {
                return (type, plural);
            }
        }

        if (Extensions.Contains(word))
        {
            return (ExtensionType(word), false);
        }

        if (word.EndsWith('s') && Extensions.Contains(word[..^1]))
        {
            return (ExtensionType(word[..^1]), true);
        }

        // A mistyped word: only the longer ones, and the short ones with a letter too many ("pdff"), are read.
        foreach (var (name, plural, type) in Words)
        {
            if ((name.Length >= MinMistypedLength || name.Length <= 3) && Fuzzy.IsWord(word, name))
            {
                return (type, plural);
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="word"/> is one of the kind words or extensions as written, not a mistyped one.</summary>
    public static bool IsExactWord(string word) =>
        Words.Any(entry => entry.Word == word) || Extensions.Contains(word)
        || (word.EndsWith('s') && Extensions.Contains(word[..^1]));

    private static FileType ExtensionType(string extension)
    {
        var dotted = "." + extension;
        foreach (var type in new[] { Pdf, WordDocument, Spreadsheet, Presentation, Video, Audio, Compressed, Text })
        {
            if (type.Extensions.Count == 1 && type.Extensions[0] == dotted)
            {
                return type;
            }
        }

        var upper = extension.ToUpperInvariant();
        return ImageFileTypes.IsImageExtension(dotted)
            ? new FileType($"{upper} image", $"{upper} images", [dotted])
            : new FileType($"{upper} file", $"{upper} files", [dotted]);
    }
}
