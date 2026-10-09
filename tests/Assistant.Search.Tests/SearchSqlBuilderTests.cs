using System.Globalization;
using System.Text;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.History;
using Assistant.Search.Index;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>The Windows Search SQL written for a query, and above all what it does with the user's text.</summary>
public sealed class SearchSqlBuilderTests
{
    private static string Build(
        string text,
        SearchResultItemType type = SearchResultItemType.File,
        bool matchContents = false,
        ExcludedFolders? excluded = null) =>
        SearchSqlBuilder.Build(
            type,
            FileSearchPlan.Create(new FileSearchQuery(text) { MatchContents = matchContents }, 50)
                ?? throw new ArgumentException("No plan for this text.", nameof(text)),
            40,
            excluded ?? ExcludedFolders.None);

    [Fact]
    public void AFileQueryStaysInTheFileScopeAndAsksForTheTopCandidates()
    {
        var sql = Build("budget");

        Assert.StartsWith("SELECT TOP 40 System.ItemPathDisplay, ", sql, StringComparison.Ordinal);
        Assert.Contains(" FROM SystemIndex WHERE SCOPE='file:' AND System.IsFolder = false", sql, StringComparison.Ordinal);
        Assert.Contains("System.ItemNameDisplay LIKE '%budget%'", sql, StringComparison.Ordinal);
        Assert.EndsWith("ORDER BY System.Search.Rank DESC, System.DateModified DESC", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoSummary", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("CONTAINS", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderQueryAsksForFolders()
    {
        Assert.Contains("System.IsFolder = true", Build("budget", SearchResultItemType.Folder), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryWordMustBeInTheName()
    {
        var sql = Build("budget 2026");

        Assert.Contains(
            "(System.ItemNameDisplay LIKE '%budget%' AND System.ItemNameDisplay LIKE '%2026%')",
            sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MatchingContentsAddsFullTextAndTheExcerptColumn()
    {
        var sql = Build("budget \"trip plan\"", matchContents: true);

        Assert.Contains("OR CONTAINS('\"budget*\" AND \"trip plan\"')", sql, StringComparison.Ordinal);
        Assert.Contains("System.Search.AutoSummary", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("100%", "'%100[%]%'")]
    [InlineData("a_b", "'%a[_]b%'")]
    [InlineData("[draft]", "'%[[]draft]%'")]
    [InlineData("x'; DROP TABLE SystemIndex; --", "'%x_; DROP TABLE SystemIndex; --%'")]
    public void TheUsersTextIsEscapedIntoALiteral(string text, string literal)
    {
        var sql = Build(text.Contains(' ') ? $"\"{text}\"" : text);

        Assert.Contains("System.ItemNameDisplay LIKE " + literal, sql, StringComparison.Ordinal);
    }

    [Fact]
    public void QuotesAndWildcardsCannotChangeTheFullTextQuery()
    {
        // The parser already reads quotes as phrase marks; a lone star or quote left in a word must not survive.
        var terms = new[] { new SearchTerm("pay*", false), new SearchTerm("a\"b c", true), new SearchTerm("***", false) };

        var condition = SearchSqlBuilder.FullTextCondition(terms);

        Assert.Equal("CONTAINS('\"pay*\" AND \"ab c\"')", condition);
    }

    [Fact]
    public void FullTextTermsWithNoLetterOrDigitLeaveNoCondition()
    {
        Assert.Null(SearchSqlBuilder.FullTextCondition([new SearchTerm("*", false)]));
    }

    [Fact]
    public void ExcludedFoldersAreLeftOutOfTheQueryAsTheFolderAndAsItsContents()
    {
        var sql = Build("budget", excluded: ExcludedFolders.Create([@"C:\Private Stuff", @"D:\100%"]));

        Assert.Contains("System.ItemUrl NOT LIKE 'file:C:/Private Stuff' AND System.ItemUrl NOT LIKE 'file:C:/Private Stuff/%'", sql, StringComparison.Ordinal);
        Assert.Contains("System.ItemUrl NOT LIKE 'file:D:/100[%]/%'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AtMostSomeExcludedFoldersAreWrittenIntoTheQuery()
    {
        var folders = Enumerable.Range(0, SearchSqlBuilder.MaxExcludedFoldersInQuery + 20).Select(number => $@"C:\Folder{number}");

        var sql = Build("budget", excluded: ExcludedFolders.Create(folders));

        Assert.Equal(SearchSqlBuilder.MaxExcludedFoldersInQuery * 2, CountOf(sql, "System.ItemUrl NOT LIKE"));
    }

    [Fact]
    public void AppsAreNotFoundInTheFileIndex()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Build("calc", SearchResultItemType.App));
    }

    [Fact]
    public void APlanWithNoCriterionIsRefused()
    {
        var plan = new FileSearchPlan { Types = [SearchResultItemType.File], Limit = 5 };

        Assert.Throws<ArgumentException>(() => SearchSqlBuilder.Build(SearchResultItemType.File, plan, 40, ExcludedFolders.None));
    }

    // ---- Step 52: the structured query --------------------------------------------------------------------------------

    private static string BuildFor(FileSearchQuery query, SearchResultItemType type = SearchResultItemType.File, ExcludedFolders? excluded = null) =>
        SearchSqlBuilder.Build(
            type,
            FileSearchPlan.Create(query, 50) ?? throw new InvalidOperationException("The query has no plan."),
            40,
            excluded ?? ExcludedFolders.None);

    private static string OrderBy(string sql) => sql[(sql.IndexOf(" ORDER BY ", StringComparison.Ordinal) + " ORDER BY ".Length)..];

    [Fact]
    public void AQueryWithNoWordsWritesOnlyItsOtherCriteria()
    {
        var sql = BuildFor(new FileSearchQuery { Extensions = [".pdf"] });

        Assert.Equal(
            "SELECT TOP 40 " + string.Join(", ", SearchColumns.Names.Take(15))
            + " FROM SystemIndex WHERE SCOPE='file:' AND System.IsFolder = false AND (System.FileExtension = '.pdf')"
            + " ORDER BY System.Search.Rank DESC, System.DateModified DESC",
            sql);
    }

    [Fact]
    public void ExtensionsAreAnyOfThemAndAreMatchedAsWholeStrings()
    {
        var sql = BuildFor(new FileSearchQuery { Extensions = ["PDF", "*.docx", ".x'y"] });

        Assert.Contains(" AND (System.FileExtension = '.pdf' OR System.FileExtension = '.docx')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE '.pdf", sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileKind.Document, "document")]
    [InlineData(FileKind.Picture, "picture")]
    [InlineData(FileKind.Video, "video")]
    [InlineData(FileKind.Music, "music")]
    public void AKindIsWrittenFromAFixedTable(FileKind kind, string name)
    {
        Assert.Contains($" AND System.Kind = '{name}'", BuildFor(new FileSearchQuery { Kind = kind }), StringComparison.Ordinal);
    }

    [Fact]
    public void DatesAreWrittenToTheSecondInUtcInTheOnlyFormatTheProviderReadsExactly()
    {
        var sql = BuildFor(new FileSearchQuery
        {
            Modified = new DateRange(
                new DateTimeOffset(2026, 1, 15, 12, 30, 30, 500, TimeSpan.FromHours(2)),
                new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)),
            Created = new DateRange(From: new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero)),
        });

        Assert.Contains(" AND System.DateModified >= '2026-01-15 10:30:30' AND System.DateModified < '2026-02-01 00:00:00'", sql, StringComparison.Ordinal);
        Assert.Contains(" AND System.DateCreated >= '2025-12-31 23:59:59'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("System.DateCreated <", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("T10:", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void SizesAreWrittenAsPlainNumbersWhateverTheCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
            var sql = BuildFor(new FileSearchQuery { Size = new SizeRange(1_234_567, 9_876_543_210) });

            Assert.Contains(" AND System.Size >= 1234567 AND System.Size <= 9876543210", sql, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void ANamedFolderIsTheScopeAndItsContentsIncludeSubfoldersUnlessAskedNotTo()
    {
        Assert.Contains(
            "WHERE SCOPE='file:C:/Users/Ada/My Docs' AND",
            BuildFor(new FileSearchQuery { Folder = @"C:\Users\Ada\My Docs\", Extensions = [".txt"] }),
            StringComparison.Ordinal);
        Assert.Contains(
            "WHERE DIRECTORY='file:C:/Users/Ada/My Docs' AND",
            BuildFor(new FileSearchQuery { Folder = @"C:\Users\Ada\My Docs", IncludeSubfolders = false }),
            StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheQuoteInAFolderPathIsEscapedBecauseTheScopeIsNeverAPattern()
    {
        var sql = BuildFor(new FileSearchQuery { Folder = @"D:\O'Brien's #1 100% [x] _y" });

        Assert.Contains("SCOPE='file:D:/O''Brien''s #1 100% [x] _y'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AFilenameMustBeInTheFileNameWithItsExtensionEachWordOnItsOwn()
    {
        var sql = BuildFor(new FileSearchQuery { Filename = "screenshot \"trip plan\" 100%" });

        Assert.Contains(
            " AND (System.FileName LIKE '%screenshot%' AND System.FileName LIKE '%trip plan%' AND System.FileName LIKE '%100[%]%')",
            sql,
            StringComparison.Ordinal);
        Assert.DoesNotContain("ItemNameDisplay LIKE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void AContentTermIsOnePhraseOverTheTextInsideTheFileOnly()
    {
        var sql = BuildFor(new FileSearchQuery { ContentTerm = "  Quarterly   budget\treview " });

        Assert.Contains(" AND CONTAINS(System.Search.Contents, '\"Quarterly budget review\"')", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("LIKE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoSummary", sql, StringComparison.Ordinal);
        Assert.Equal(15, sql.Split(" FROM ")[0].Split(',').Length);
    }

    [Fact]
    public void AContentTermAndTheOlderMatchContentsFlagAreDifferentThings()
    {
        var sql = BuildFor(new FileSearchQuery("merger") { MatchContents = true, ContentTerm = "trip plan" });

        Assert.Contains("System.Search.AutoSummary", sql, StringComparison.Ordinal);
        Assert.Contains("OR CONTAINS('\"merger*\"')", sql, StringComparison.Ordinal);
        Assert.Contains("AND CONTAINS(System.Search.Contents, '\"trip plan\"')", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryCriterionIsAnAndAndTheTextsOrIsKeptInsideItsOwnParentheses()
    {
        var sql = BuildFor(new FileSearchQuery("merger")
        {
            MatchContents = true,
            Filename = "notes",
            Extensions = [".txt"],
            Size = new SizeRange(MaxBytes: 100),
        });

        Assert.Contains(
            "AND ((System.ItemNameDisplay LIKE '%merger%') OR CONTAINS('\"merger*\"')) "
            + "AND ((System.FileName LIKE '%notes%' OR System.FileName LIKE '%note_s%')) "
            + "AND (System.FileExtension = '.txt') AND System.Size <= 100 ORDER BY",
            sql,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(FileSearchOrder.Relevance, "System.Search.Rank DESC, System.DateModified DESC")]
    [InlineData(FileSearchOrder.ModifiedDescending, "System.DateModified DESC, System.ItemUrl ASC")]
    [InlineData(FileSearchOrder.ModifiedAscending, "System.DateModified ASC, System.ItemUrl ASC")]
    [InlineData(FileSearchOrder.CreatedDescending, "System.DateCreated DESC, System.ItemUrl ASC")]
    [InlineData(FileSearchOrder.CreatedAscending, "System.DateCreated ASC, System.ItemUrl ASC")]
    [InlineData(FileSearchOrder.NameAscending, "System.FileName ASC, System.ItemUrl ASC")]
    [InlineData(FileSearchOrder.NameDescending, "System.FileName DESC, System.ItemUrl ASC")]
    [InlineData(FileSearchOrder.SizeDescending, "System.Size DESC, System.ItemUrl ASC")]
    [InlineData(FileSearchOrder.SizeAscending, "System.Size ASC, System.ItemUrl ASC")]
    public void EveryOrderHasItsOwnOrderByAndAllButRelevanceEndInTheUrlSoTiesAreStable(FileSearchOrder order, string orderBy)
    {
        Assert.Equal(orderBy, OrderBy(BuildFor(new FileSearchQuery("budget") { Order = order })));
    }

    [Fact]
    public void TheLocationProbeAsksForOneItemUnderTheFolderAndNothingElse()
    {
        var plan = FileSearchPlan.Create(new FileSearchQuery { Folder = @"C:\Data\it's", ContentTerm = "secret words" }, 50)!;

        Assert.Equal(
            "SELECT TOP 1 System.ItemUrl FROM SystemIndex WHERE SCOPE='file:C:/Data/it''s'",
            SearchSqlBuilder.BuildLocationProbe(plan.Scope!));
        Assert.DoesNotContain("secret", SearchSqlBuilder.BuildLocationProbe(plan.Scope!), StringComparison.Ordinal);
    }

    [Fact]
    public void ExcludedFoldersAreStillLeftOutOfAFolderSearchAndBeforeTheOrder()
    {
        var sql = BuildFor(
            new FileSearchQuery { Folder = @"C:\Docs", Order = FileSearchOrder.ModifiedDescending, Extensions = [".txt"] },
            excluded: ExcludedFolders.Create([@"C:\Docs\Private"]));

        Assert.Contains("AND System.ItemUrl NOT LIKE 'file:C:/Docs/Private/%' ORDER BY System.DateModified DESC", sql, StringComparison.Ordinal);
    }

    // What the caller typed can only ever be inside a literal: take every literal out of a query, and the rest is the same
    // whatever was typed.
    [Fact]
    public void HostileTextInEveryFieldStaysInsideLiteralsAndTheQueryStaysTheSameShape()
    {
        // One phrase each, so that the hostile query has as many terms as the plain one and only their text differs. A word with
        // an apostrophe is matched two ways (see NameText), so the plain words have one too, and the shape is the same.
        const string Hostile = "x'; DROP TABLE SystemIndex; -- ' OR '1'='1 %_[ * ) UNION SELECT 1";
        var plain = new FileSearchQuery("alpha's")
        {
            MatchContents = true,
            Filename = "beta's",
            ContentTerm = "gamma delta",
            Extensions = [".txt"],
            Folder = @"C:\Docs\epsilon",
        };
        var hostile = plain with
        {
            Text = $"\"{Hostile}\"",
            Filename = $"\"{Hostile}\"",
            ContentTerm = Hostile + " \" more \"",
            Folder = @"C:\Docs\" + Hostile,
        };

        var plainSql = BuildFor(plain);
        var hostileSql = BuildFor(hostile);

        Assert.Equal(WithoutLiterals(plainSql), WithoutLiterals(hostileSql));
        Assert.DoesNotContain("DROP", WithoutLiterals(hostileSql), StringComparison.Ordinal);
        Assert.Contains("DROP", hostileSql, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExtensionThatIsNotSafeAsAnExtensionNeverReachesTheQuery()
    {
        Assert.Null(FileSearchPlan.Create(new FileSearchQuery { Extensions = ["a'b", "x'; DROP", "a b", "a/b"] }, 50));
    }

    /// <summary>The SQL with the inside of every string literal removed; a doubled quote is part of the literal.</summary>
    private static string WithoutLiterals(string sql)
    {
        var rest = new StringBuilder();
        var inLiteral = false;
        for (var index = 0; index < sql.Length; index++)
        {
            var character = sql[index];
            if (inLiteral)
            {
                if (character != '\'')
                {
                    continue;
                }

                if (index + 1 < sql.Length && sql[index + 1] == '\'')
                {
                    index++;
                    continue;
                }

                inLiteral = false;
                rest.Append("''");
                continue;
            }

            if (character == '\'')
            {
                inLiteral = true;
                continue;
            }

            rest.Append(character);
        }

        Assert.False(inLiteral, "a string literal was left open");
        return rest.ToString();
    }

    private static int CountOf(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
