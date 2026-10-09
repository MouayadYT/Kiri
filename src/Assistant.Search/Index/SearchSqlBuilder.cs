using System.Globalization;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.History;

namespace Assistant.Search.Index;

/// <summary>
/// Writes the Windows Search SQL for one kind of item from a <see cref="FileSearchPlan"/>. The provider takes no parameters,
/// so everything the caller typed goes into the query as a literal and is escaped here, in one place: a quote, a wildcard of
/// <c>LIKE</c> or a character of the full-text syntax in a name or a phrase must never change what the query means. Nothing
/// else is text of the caller's: numbers are written from numbers, times from times, kinds and orders from a fixed table.
/// </summary>
internal static class SearchSqlBuilder
{
    /// <summary>The most excluded folders written into a query; the rest are removed from the results afterwards.</summary>
    public const int MaxExcludedFoldersInQuery = 50;

    /// <summary>
    /// The query for <paramref name="type"/> (a file or a folder): items in the plan's scope (the <c>file:</c> scope, or its
    /// folder) that meet every criterion of the plan. The name must hold every word of the text or, when the plan matches
    /// contents, its text or properties do. It returns the <paramref name="top"/> that come first in the plan's order, with the
    /// properties of <see cref="SearchColumns"/>.
    /// </summary>
    public static string Build(SearchResultItemType type, FileSearchPlan plan, int top, ExcludedFolders excluded)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (type is not (SearchResultItemType.File or SearchResultItemType.Folder))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Only files and folders are found in the file index.");
        }

        if (!plan.HasCriteria)
        {
            throw new ArgumentException("A search needs at least one criterion.", nameof(plan));
        }

        var count = SearchColumns.CountFor(plan.MatchContents);
        var sql = new StringBuilder("SELECT TOP ").Append(top).Append(' ');
        for (var index = 0; index < count; index++)
        {
            sql.Append(index == 0 ? "" : ", ").Append(SearchColumns.Names[index]);
        }

        sql.Append(" FROM SystemIndex WHERE ").Append(ScopeCondition(plan.Scope))
            .Append(" AND System.IsFolder = ").Append(type == SearchResultItemType.Folder ? "true" : "false");

        AppendWords(sql, plan);

        if (FilenameCondition(plan.FilenameTerms) is { } filename)
        {
            sql.Append(" AND (").Append(filename).Append(')');
        }

        if (plan.ContentPhrase is { } phrase)
        {
            // Only the text inside the file: a name or a property that has the phrase does not count.
            sql.Append(" AND CONTAINS(System.Search.Contents, ").Append(Literal("\"" + phrase + "\"")).Append(')');
        }

        if (plan.Extensions.Count > 0)
        {
            sql.Append(" AND (")
                .Append(string.Join(" OR ", plan.Extensions.Select(extension => "System.FileExtension = " + Literal(extension))))
                .Append(')');
        }

        if (plan.Kind is { } kind)
        {
            sql.Append(" AND System.Kind = ").Append(Literal(KindName(kind)));
        }

        AppendTimes(sql, "System.DateModified", plan.Modified);
        AppendTimes(sql, "System.DateCreated", plan.Created);
        if (plan.MinBytes is { } min)
        {
            sql.Append(" AND System.Size >= ").Append(min.ToString(CultureInfo.InvariantCulture));
        }

        if (plan.MaxBytes is { } max)
        {
            sql.Append(" AND System.Size <= ").Append(max.ToString(CultureInfo.InvariantCulture));
        }

        foreach (var (folder, inside) in excluded.Urls(MaxExcludedFoldersInQuery))
        {
            sql.Append(" AND System.ItemUrl NOT LIKE ").Append(Literal(EscapeLike(folder)))
                .Append(" AND System.ItemUrl NOT LIKE ").Append(Literal(EscapeLike(inside) + "%"));
        }

        return sql.Append(" ORDER BY ").Append(OrderBy(plan.Order)).ToString();
    }

    /// <summary>
    /// A query for one item of the plan's folder: it has a row when the index holds anything under the folder, and none when
    /// the folder is not indexed (or is empty). It asks nothing of the plan but its folder.
    /// </summary>
    public static string BuildLocationProbe(FolderScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        return "SELECT TOP 1 System.ItemUrl FROM SystemIndex WHERE " + ScopeCondition(scope);
    }

    /// <summary>
    /// The scope of a query: everything in the <c>file:</c> scope, or a folder in it. Only the quote of the folder's path
    /// means anything to the provider here; the path is never a pattern (probed on the real index: a <c>%</c>, a <c>#</c>,
    /// brackets and accents in a path are all matched as they are).
    /// </summary>
    private static string ScopeCondition(FolderScope? scope) => scope is null
        ? "SCOPE='file:'"
        : (scope.IncludeSubfolders ? "SCOPE=" : "DIRECTORY=") + Literal(scope.Url);

    /// <summary>The words of the text: in the name, or, with contents, in the name or in the text and properties.</summary>
    private static void AppendWords(StringBuilder sql, FileSearchPlan plan)
    {
        var contents = plan.MatchContents ? FullTextCondition(plan.Terms) : null;
        var name = NameCondition(plan.Terms);
        if (name is null && contents is null)
        {
            return;
        }

        sql.Append(" AND (");
        sql.Append(name is not null && contents is not null ? $"({name}) OR {contents}" : name ?? contents);
        sql.Append(')');
    }

    private static void AppendTimes(StringBuilder sql, string property, TimeBounds bounds)
    {
        // The one format the provider reads to the second and as UTC: a date, a space, a time. Its ISO 8601 form with a T
        // silently drops the time of day (probed on the real index), so it is never written.
        if (bounds.From is { } from)
        {
            sql.Append(" AND ").Append(property).Append(" >= ").Append(Literal(TimeText(from)));
        }

        if (bounds.To is { } to)
        {
            sql.Append(" AND ").Append(property).Append(" < ").Append(Literal(TimeText(to)));
        }
    }

    private static string TimeText(DateTime utc) => utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string KindName(FileKind kind) => kind switch
    {
        FileKind.Document => "document",
        FileKind.Picture => "picture",
        FileKind.Video => "video",
        FileKind.Music => "music",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a kind of file."),
    };

    /// <summary>
    /// The <c>ORDER BY</c> of an order. Relevance asks the index for its rank and then the newest: it gives a name match no
    /// rank, so the service orders those by how well the name fits. Every other order names its property and ends with the
    /// item's URL, so that items with equal values always come in the same order.
    /// </summary>
    private static string OrderBy(FileSearchOrder order) => order switch
    {
        FileSearchOrder.ModifiedDescending => "System.DateModified DESC, System.ItemUrl ASC",
        FileSearchOrder.ModifiedAscending => "System.DateModified ASC, System.ItemUrl ASC",
        FileSearchOrder.CreatedDescending => "System.DateCreated DESC, System.ItemUrl ASC",
        FileSearchOrder.CreatedAscending => "System.DateCreated ASC, System.ItemUrl ASC",
        FileSearchOrder.NameAscending => "System.FileName ASC, System.ItemUrl ASC",
        FileSearchOrder.NameDescending => "System.FileName DESC, System.ItemUrl ASC",
        FileSearchOrder.SizeDescending => "System.Size DESC, System.ItemUrl ASC",
        FileSearchOrder.SizeAscending => "System.Size ASC, System.ItemUrl ASC",
        _ => "System.Search.Rank DESC, System.DateModified DESC",
    };

    /// <summary>Every term as part of the item's name, in any order: <c>name LIKE '%a%' AND name LIKE '%b%'</c>.</summary>
    internal static string? NameCondition(IReadOnlyList<SearchTerm> terms) => LikeCondition("System.ItemNameDisplay", terms);

    /// <summary>Every term as part of the file's name with its extension, in any order.</summary>
    internal static string? FilenameCondition(IReadOnlyList<SearchTerm> terms) => LikeCondition("System.FileName", terms);

    private static string? LikeCondition(string property, IReadOnlyList<SearchTerm> terms)
    {
        // A word is one pattern, or two when the name may spell its apostrophe another way (see NameText).
        var parts = terms
            .Where(term => term.Text.Length > 0)
            .Select(term => NameText.LikePatterns(term.Text).Select(pattern => property + " LIKE " + Literal(pattern)).ToArray())
            .Select(likes => likes.Length == 1 ? likes[0] : "(" + string.Join(" OR ", likes) + ")")
            .ToArray();
        return parts.Length == 0 ? null : string.Join(" AND ", parts);
    }

    /// <summary>
    /// Every term in the item's text or properties: a word as the start of a word (<c>"budget*"</c>), a phrase as it was
    /// typed. Null when no term has anything the full-text syntax can take.
    /// </summary>
    internal static string? FullTextCondition(IReadOnlyList<SearchTerm> terms)
    {
        var parts = new List<string>();
        foreach (var term in terms)
        {
            // Inside a quoted phrase only the quote itself, and the wildcard, mean anything to the parser.
            var text = term.Text.Replace("\"", "", StringComparison.Ordinal).Replace("*", "", StringComparison.Ordinal).Trim();
            if (!text.Any(char.IsLetterOrDigit))
            {
                continue;
            }

            parts.Add(term.IsPhrase ? $"\"{text}\"" : $"\"{text}*\"");
        }

        return parts.Count == 0 ? null : "CONTAINS(" + Literal(string.Join(" AND ", parts)) + ")";
    }

    /// <summary>Escapes the characters <c>LIKE</c> treats as a pattern, so the text is matched as it is.</summary>
    internal static string EscapeLike(string text)
    {
        var escaped = new StringBuilder(text.Length + 8);
        foreach (var character in text)
        {
            switch (character)
            {
                case '%':
                    escaped.Append("[%]");
                    break;
                case '_':
                    escaped.Append("[_]");
                    break;
                case '[':
                    escaped.Append("[[]");
                    break;
                default:
                    escaped.Append(character);
                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>A string literal: in single quotes, with each quote in it doubled.</summary>
    internal static string Literal(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
}
