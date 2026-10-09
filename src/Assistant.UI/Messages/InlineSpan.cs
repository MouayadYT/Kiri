namespace Assistant.UI.Messages;

/// <summary>How a run of an answer's text is emphasized.</summary>
[Flags]
public enum InlineStyle
{
    /// <summary>Plain text.</summary>
    None = 0,

    /// <summary>Strong emphasis: <c>**text**</c>.</summary>
    Bold = 1,

    /// <summary>Emphasis: <c>*text*</c>.</summary>
    Italic = 2,

    /// <summary>Struck through: <c>~~text~~</c>.</summary>
    Strikethrough = 4,

    /// <summary>Inline code: <c>`text`</c>.</summary>
    Code = 8,

    /// <summary>A link, whose address is <see cref="InlineSpan.Url"/>.</summary>
    Link = 16,
}

/// <summary>A run of an answer's text with one style, as <see cref="InlineMarkdown"/> reads it.</summary>
/// <param name="Text">The words, without the marks that made their style.</param>
/// <param name="Style">How the words are emphasized.</param>
/// <param name="Url">The address of a <see cref="InlineStyle.Link"/> run; otherwise <see langword="null"/>.</param>
public sealed record InlineSpan(string Text, InlineStyle Style = InlineStyle.None, string? Url = null)
{
    // Spans hold chat content (PROJECT_SPEC §3.2), so their text never reaches ToString, and so never a log.
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Style = {Style}");
        return true;
    }
}
