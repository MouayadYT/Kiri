using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Assistant.Data.Persistence;

/// <summary>
/// Lower-cases text and takes the accents off it, the way a search compares words, so <c>Café</c> and <c>cafe</c> are
/// the same. SQLite folds only ASCII by itself; the connection factory hands it this as the SQL function
/// <c>fold(text)</c> for the searches its full-text index cannot answer.
/// </summary>
internal static class TextFolding
{
    /// <summary>The name of the SQL function.</summary>
    public const string FunctionName = "fold";

    /// <summary>Returns <paramref name="text"/> in lower case, without the marks that sit on letters.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                folded.Append(character);
            }
        }

        return folded.ToString().ToLowerInvariant();
    }

    /// <summary>Makes <c>fold(text)</c> available to the SQL that runs on <paramref name="connection"/>.</summary>
    public static void Register(SqliteConnection connection) =>
        connection.CreateFunction<string?, string?>(
            FunctionName,
            static text => text is null ? null : Fold(text),
            isDeterministic: true);
}
