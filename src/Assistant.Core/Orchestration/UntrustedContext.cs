using System.Text;
using System.Text.RegularExpressions;

namespace Assistant.Core.Orchestration;

/// <summary>
/// Marks a piece of the user's context in a prompt as data (PROJECT_SPEC P9): it is wrapped in a tag that carries an
/// id, what kind of context it is and its label, and cannot close the tag itself.
/// </summary>
public static partial class UntrustedContext
{
    /// <summary>The tag that wraps each piece, named in <see cref="AssistantInstructions.UntrustedContextGuidance"/>.</summary>
    public const string TagName = "untrusted_context";

    private const int MaxLabelLength = 100;

    // The most of an extra attribute that is kept: a page's title or address is longer than a label.
    private const int MaxAttributeLength = 300;

    /// <summary>
    /// Wraps <paramref name="text"/> in the tag. Any tag-like text inside it is defused, so content that imitates the
    /// closing tag stays inside the block.
    /// </summary>
    /// <param name="id">Identifies the piece within its message.</param>
    /// <param name="kind">What kind of context it is, such as <c>selection</c> or <c>file</c>.</param>
    /// <param name="label">The item's display name, or <see langword="null"/> for none.</param>
    /// <param name="text">The captured content.</param>
    /// <param name="attributes">What else is known about where the content came from, such as a web page's title and address, as
    /// attribute names and values; a blank value is left out, and a value is cleaned like the label.</param>
    public static string Wrap(
        int id, string kind, string? label, string text, IReadOnlyList<KeyValuePair<string, string?>>? attributes = null)
    {
        var builder = new StringBuilder();
        builder.Append('<').Append(TagName).Append(" id=\"").Append(id).Append("\" kind=\"").Append(kind).Append('"');
        if (CleanLabel(label) is { Length: > 0 } clean)
        {
            builder.Append(" name=\"").Append(clean).Append('"');
        }

        foreach (var (name, value) in attributes ?? [])
        {
            if (CleanLabel(value, MaxAttributeLength) is { Length: > 0 } cleanValue)
            {
                builder.Append(' ').Append(name).Append("=\"").Append(cleanValue).Append('"');
            }
        }

        builder.Append(">\n").Append(Defuse(text).TrimEnd()).Append("\n</").Append(TagName).Append('>');
        return builder.ToString();
    }

    // "<" before our tag name, with or without a slash or spaces, becomes "&lt;", which reads the same and cannot
    // open or close a block.
    private static string Defuse(string text) => TagLike().Replace(text, "&lt;");

    // A label sits inside an attribute: no quotes, angle brackets or line breaks, one line, and not long.
    private static string CleanLabel(string? label, int maxLength = MaxLabelLength)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(label.Length, maxLength));
        foreach (var character in label)
        {
            if (builder.Length >= maxLength)
            {
                break;
            }

            if (character is '<' or '>')
            {
                continue;
            }

            builder.Append(character switch
            {
                '"' => '\'',
                _ when char.IsControl(character) => ' ',
                _ => character,
            });
        }

        return WhiteSpace().Replace(builder.ToString(), " ").Trim();
    }

    [GeneratedRegex($@"<(?=\s*/?\s*{TagName})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TagLike();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
