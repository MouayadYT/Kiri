namespace Assistant.Documents.Extraction;

internal static class NewLines
{
    /// <summary>One kind of line break: <c>\r\n</c> and a lone <c>\r</c> become <c>\n</c>, so lines are counted the same in every file.</summary>
    public static string Normalize(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
}
