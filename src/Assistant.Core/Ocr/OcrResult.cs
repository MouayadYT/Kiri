using System.Text;

namespace Assistant.Core.Ocr;

/// <summary>Where text is in an image, in pixels of the image, from its top left corner.</summary>
/// <param name="X">Pixels from the left edge to the box's left.</param>
/// <param name="Y">Pixels from the top edge to the box's top.</param>
/// <param name="Width">The box's width.</param>
/// <param name="Height">The box's height.</param>
public readonly record struct OcrBox(double X, double Y, double Width, double Height)
{
    /// <summary>The box's right edge.</summary>
    public double Right => X + Width;

    /// <summary>The box's bottom edge.</summary>
    public double Bottom => Y + Height;

    /// <summary>The smallest box that holds this one and <paramref name="other"/>.</summary>
    public OcrBox Union(OcrBox other)
    {
        var left = Math.Min(X, other.X);
        var top = Math.Min(Y, other.Y);
        return new OcrBox(left, top, Math.Max(Right, other.Right) - left, Math.Max(Bottom, other.Bottom) - top);
    }
}

/// <summary>One word recognized in an image, and where it is.</summary>
/// <param name="Text">The word.</param>
/// <param name="Box">Where it is.</param>
public sealed record OcrWord(string Text, OcrBox Box);

/// <summary>One line of text recognized in an image, and where it is.</summary>
/// <param name="Text">The line, its words separated by spaces.</param>
/// <param name="Box">Where it is: the smallest box that holds its words.</param>
/// <param name="Words">Its words, left to right, each with its own place.</param>
public sealed record OcrLine(string Text, OcrBox Box, IReadOnlyList<OcrWord> Words);

/// <summary>
/// The text a local OCR engine read in an image (PROJECT_SPEC §4.6): its lines in reading order, each with where it is, in pixels of the
/// image that was read. It is private content (§3.2) held in memory only; <see cref="ToString"/>, and so any log, holds counts alone.
/// </summary>
/// <param name="Lines">The lines, from the top of the image to the bottom.</param>
/// <param name="ImageWidth">The width of the image that was read, in pixels.</param>
/// <param name="ImageHeight">The height of the image that was read, in pixels.</param>
/// <param name="Language">The language the engine read it in, as a tag such as <c>en-US</c>, or <see langword="null"/> when it does not say.</param>
public sealed record OcrResult(IReadOnlyList<OcrLine> Lines, int ImageWidth, int ImageHeight, string? Language)
{
    /// <summary>Whether no text was found.</summary>
    public bool IsEmpty => Lines.Count == 0 || Lines.All(line => string.IsNullOrWhiteSpace(line.Text));

    /// <summary>The text, one line to a row, in reading order.</summary>
    public string Text => string.Join('\n', Lines.Select(line => line.Text));

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Lines = {Lines.Count}, ImageWidth = {ImageWidth}, ImageHeight = {ImageHeight}, Language = {Language}");
        return true;
    }
}
