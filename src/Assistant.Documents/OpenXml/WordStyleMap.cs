using System.Globalization;
using System.Xml;

namespace Assistant.Documents.OpenXml;

/// <summary>
/// Which paragraph styles of a Word document are headings, and of what level. A style is a heading when it says so with an outline
/// level, or is the built-in "heading 1" to "heading 9" (Word stores that name in English whatever the language of the program, so
/// a style called "Überschrift 1" is still found), and it may get that from the style it is based on.
/// </summary>
internal sealed class WordStyleMap
{
    private const int MaxBasedOnDepth = 10;

    private readonly Dictionary<string, StyleInfo> _styles = new(StringComparer.Ordinal);

    public static WordStyleMap Empty { get; } = new();

    /// <summary>Reads the styles part (<c>word/styles.xml</c>).</summary>
    public static WordStyleMap Read(XmlReader reader, CancellationToken cancellationToken)
    {
        var map = new WordStyleMap();
        while (!reader.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeXml.CheckDepth(reader);

            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "style" && Ooxml.IsWordprocessing(reader.NamespaceURI))
            {
                var id = reader.GetAttribute("styleId", reader.NamespaceURI);
                using (var style = reader.ReadSubtree())
                {
                    map.ReadStyle(style, id);
                }

                reader.Read();
                continue;
            }

            reader.Read();
        }

        return map;
    }

    /// <summary>The heading level (1 is the broadest) of a paragraph style, or 0 when it is not a heading.</summary>
    public int HeadingLevel(string? styleId)
    {
        for (var hop = 0; hop < MaxBasedOnDepth && styleId is not null && _styles.TryGetValue(styleId, out var style); hop++)
        {
            if (style.OutlineLevel is { } outline)
            {
                return outline < 9 ? outline + 1 : 0;
            }

            if (style.Name is { } name && name.StartsWith("heading ", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(name.AsSpan(8), NumberStyles.None, CultureInfo.InvariantCulture, out var level) && level is >= 1 and <= 9)
            {
                return level;
            }

            styleId = style.BasedOn;
        }

        return 0;
    }

    private void ReadStyle(XmlReader style, string? id)
    {
        string? name = null;
        string? basedOn = null;
        int? outline = null;

        while (style.Read())
        {
            if (style.NodeType != XmlNodeType.Element || !Ooxml.IsWordprocessing(style.NamespaceURI))
            {
                continue;
            }

            switch (style.LocalName)
            {
                case "name":
                    name = style.GetAttribute("val", style.NamespaceURI);
                    break;
                case "basedOn":
                    basedOn = style.GetAttribute("val", style.NamespaceURI);
                    break;
                case "outlineLvl":
                    if (int.TryParse(style.GetAttribute("val", style.NamespaceURI), NumberStyles.None, CultureInfo.InvariantCulture, out var level))
                    {
                        outline = level;
                    }

                    break;
            }
        }

        if (id is not null)
        {
            _styles[id] = new StyleInfo(name, basedOn, outline);
        }
    }

    private sealed record StyleInfo(string? Name, string? BasedOn, int? OutlineLevel);
}
