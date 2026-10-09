using System.Globalization;
using System.Xml;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;
using DocumentFormat.OpenXml.Packaging;

namespace Assistant.Documents.OpenXml;

/// <summary>What the core properties of an Office file say about it (<c>docProps/core.xml</c>): its title, author and dates.</summary>
internal static class PackageMetadata
{
    private const int MaxLength = 200;

    private const string DublinCore = "http://purl.org/dc/elements/1.1/";
    private const string DublinCoreTerms = "http://purl.org/dc/terms/";

    public static DocumentMetadata Read(OpenXmlPackage package, DocumentReadLimits limits, CancellationToken cancellationToken)
    {
        if (package.GetPartsOfType<CoreFilePropertiesPart>().FirstOrDefault() is not { } part)
        {
            return new DocumentMetadata();
        }

        string? title = null;
        string? author = null;
        DateTimeOffset? created = null;
        DateTimeOffset? modified = null;

        using var xml = SafeXml.Create(part.GetStream(FileMode.Open, FileAccess.Read), limits.MaxExpandedBytes);
        while (!xml.EOF)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SafeXml.CheckDepth(xml);

            if (xml.NodeType == XmlNodeType.Element && !xml.IsEmptyElement)
            {
                switch (xml.NamespaceURI, xml.LocalName)
                {
                    case (DublinCore, "title"):
                        title = Property(xml.ReadElementContentAsString());
                        continue;
                    case (DublinCore, "creator"):
                        author = Property(xml.ReadElementContentAsString());
                        continue;
                    case (DublinCoreTerms, "created"):
                        created = Date(xml.ReadElementContentAsString());
                        continue;
                    case (DublinCoreTerms, "modified"):
                        modified = Date(xml.ReadElementContentAsString());
                        continue;
                }
            }

            xml.Read();
        }

        return new DocumentMetadata { Title = title, Author = author, CreatedAt = created, ModifiedAt = modified };
    }

    private static string? Property(string? value)
    {
        var clean = TextCleaner.Clean(value).Replace('\n', ' ');
        return clean.Length == 0 ? null : clean.Length <= MaxLength ? clean : clean[..MaxLength];
    }

    // Dates in a package are W3CDTF, UTC with a Z; one written with no zone is taken as UTC too.
    private static DateTimeOffset? Date(string value) =>
        DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
        && date.Year > 1
            ? date
            : null;
}
