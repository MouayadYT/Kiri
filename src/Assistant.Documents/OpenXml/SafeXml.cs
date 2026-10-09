using System.Xml;
using Assistant.Core.Documents;
using Assistant.Documents.Extraction;

namespace Assistant.Documents.OpenXml;

/// <summary>
/// How every XML part of a package is read: forward only (so the size of a part is not the size of what is in memory), with a
/// document type declaration refused (so no entity, internal or external, is ever expanded or fetched: a file cannot make the
/// reader open another file or an address), no resolver, a cap on characters, and a cap on nesting, so a part built to
/// exhaust the stack or the memory is a <see cref="DocumentReadStatus.Corrupt"/> file and not a crash.
/// </summary>
internal static class SafeXml
{
    /// <summary>The deepest nesting read. Office files nest a few dozen levels at most (tables in tables, groups of shapes).</summary>
    public const int MaxDepth = 256;

    public static XmlReader Create(Stream stream, long maxCharacters)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maxCharacters,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = false,
            CloseInput = true,
        };
        return XmlReader.Create(stream, settings);
    }

    public static void CheckDepth(XmlReader reader)
    {
        if (reader.Depth > MaxDepth)
        {
            throw new DocumentReadException(DocumentReadStatus.Corrupt);
        }
    }
}
