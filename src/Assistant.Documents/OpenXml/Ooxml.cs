namespace Assistant.Documents.OpenXml;

/// <summary>
/// The XML namespaces of Office Open XML, in the form Word, PowerPoint and Office 2007 and later write (transitional) and in the
/// ISO "strict" form, so a file is read whichever it is.
/// </summary>
internal static class Ooxml
{
    public static bool IsWordprocessing(string ns) =>
        ns is "http://schemas.openxmlformats.org/wordprocessingml/2006/main" or "http://purl.oclc.org/ooxml/wordprocessingml/main";

    public static bool IsMath(string ns) =>
        ns is "http://schemas.openxmlformats.org/officeDocument/2006/math" or "http://purl.oclc.org/ooxml/officeDocument/math";

    public static bool IsDrawing(string ns) =>
        ns is "http://schemas.openxmlformats.org/drawingml/2006/main" or "http://purl.oclc.org/ooxml/drawingml/main";

    public static bool IsPresentation(string ns) =>
        ns is "http://schemas.openxmlformats.org/presentationml/2006/main" or "http://purl.oclc.org/ooxml/presentationml/main";

    public static bool IsDiagram(string ns) =>
        ns is "http://schemas.openxmlformats.org/drawingml/2006/diagram" or "http://purl.oclc.org/ooxml/drawingml/diagram";

    public static bool IsMarkupCompatibility(string ns) =>
        ns == "http://schemas.openxmlformats.org/markup-compatibility/2006";

    public static bool IsRelationships(string ns) =>
        ns is "http://schemas.openxmlformats.org/officeDocument/2006/relationships" or "http://purl.oclc.org/ooxml/officeDocument/relationships";

    public static bool IsExtendedProperties(string ns) =>
        ns == "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
}
