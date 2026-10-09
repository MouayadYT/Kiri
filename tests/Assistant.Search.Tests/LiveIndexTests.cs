using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Search.Tests;

/// <summary>
/// The service against this PC's real Windows Search index. They check what must hold whatever the index holds, and do
/// nothing on a PC where Windows Search is off or its index is empty, because the outcome then says nothing about the code.
/// </summary>
public sealed class LiveIndexTests
{
    // A word that most PCs have many files named after, and that the index finds quickly.
    private const string Word = "test";

    private static WindowsFileSearchService Service(params string[] excluded) =>
        new(new FakeSettings(excluded), NullLogger<WindowsFileSearchService>.Instance);

    /// <summary>Runs <paramref name="search"/>; null when Windows Search is not available here.</summary>
    private static async Task<IReadOnlyList<SearchResultItem>?> TryAsync(
        WindowsFileSearchService service,
        FileSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await service.SearchAsync(query, cancellationToken);
        }
        catch (FileSearchException exception) when (exception.Failure == FileSearchFailure.IndexUnavailable)
        {
            return null;
        }
    }

    [Fact]
    public async Task RealResultsAreNormalizedAndKeepToTheirLimit()
    {
        var results = await TryAsync(Service(), new FileSearchQuery(Word) { MaxResultsPerType = 5, MatchContents = false });
        if (results is null)
        {
            return;
        }

        Assert.True(results.Count(item => item.Type == SearchResultItemType.File) <= 5);
        Assert.True(results.Count(item => item.Type == SearchResultItemType.Folder) <= 5);
        Assert.DoesNotContain(results, item => item.Type == SearchResultItemType.App);
        Assert.Equal(results.Count, results.Select(item => item.Path.ToUpperInvariant()).Distinct().Count());

        foreach (var item in results)
        {
            Assert.True(Path.IsPathFullyQualified(item.Path), "the path is a full path");
            Assert.False(string.IsNullOrEmpty(item.DisplayName));
            Assert.Contains(Word, item.DisplayName, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(item.ModifiedAt);
            Assert.Equal(TimeSpan.Zero, item.ModifiedAt!.Value.Offset);
            if (item.Type == SearchResultItemType.Folder)
            {
                Assert.Null(item.Extension);
                Assert.Null(item.SizeBytes);
            }
            else if (item.Extension is not null)
            {
                Assert.StartsWith(".", item.Extension, StringComparison.Ordinal);
                Assert.Equal(item.Extension.ToLowerInvariant(), item.Extension);
            }
        }
    }

    [Fact]
    public async Task TheIndexsSizesAndUtcDatesAgreeWithTheFileSystem()
    {
        var results = await TryAsync(
            Service(),
            new FileSearchQuery(Word) { Types = [SearchResultItemType.File], MaxResultsPerType = 50 });
        if (results is null)
        {
            return;
        }

        var checkedFiles = 0;
        foreach (var item in results)
        {
            var file = new FileInfo(item.Path);

            // The index can be behind the disk; compare only files it has caught up with.
            if (!file.Exists || Math.Abs((file.LastWriteTimeUtc - item.ModifiedAt!.Value.UtcDateTime).TotalSeconds) > 2)
            {
                continue;
            }

            Assert.Equal(file.Length, item.SizeBytes);
            Assert.True(Math.Abs((file.CreationTimeUtc - item.CreatedAt!.Value.UtcDateTime).TotalSeconds) < 2);
            Assert.Equal(file.Name, item.DisplayName);
            checkedFiles++;
        }

        Assert.True(checkedFiles > 0 || results.Count == 0, "no result the index had caught up with");
    }

    [Fact]
    public async Task ResultsInsideAnExcludedFolderAreNotReturned()
    {
        var first = await TryAsync(Service(), new FileSearchQuery(Word) { Types = [SearchResultItemType.File], MaxResultsPerType = 10 });
        if (first is not { Count: > 0 })
        {
            return;
        }

        var folder = Path.GetDirectoryName(first[0].Path)!;
        var second = await Service(folder).SearchAsync(
            new FileSearchQuery(Word) { Types = [SearchResultItemType.File, SearchResultItemType.Folder], MaxResultsPerType = 50 });

        Assert.DoesNotContain(
            second,
            item => item.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                && (item.Path.Length == folder.Length || item.Path[folder.Length] == Path.DirectorySeparatorChar));
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("100%")]
    [InlineData("a_b")]
    [InlineData("[draft")]
    [InlineData("draft]")]
    [InlineData("x'; DROP TABLE SystemIndex; --")]
    [InlineData("\"unclosed phrase")]
    [InlineData("*")]
    [InlineData("a* OR b*")]
    [InlineData("NEAR(a, b)")]
    [InlineData("(((")]
    [InlineData("C:\\Users\\*.txt")]
    [InlineData("üñïçødé 日本語 😀")]
    [InlineData("AND")]
    [InlineData("-not")]
    public async Task HostileOrOddTextIsJustText(string text)
    {
        foreach (var matchContents in new[] { false, true })
        {
            try
            {
                await Service().SearchAsync(new FileSearchQuery(text) { MatchContents = matchContents, MaxResultsPerType = 3 });
            }
            catch (FileSearchException exception) when (exception.Failure == FileSearchFailure.IndexUnavailable)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task ASearchThatIsCancelledStops()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Service().SearchAsync(new FileSearchQuery(Word), cancelled.Token));
    }

    [Fact]
    public async Task ContentMatchesCarryAnExcerptOrNone()
    {
        var results = await TryAsync(Service(), new FileSearchQuery("the") { MatchContents = true, MaxResultsPerType = 5 });
        if (results is null)
        {
            return;
        }

        foreach (var item in results.Where(item => item.Snippet is not null))
        {
            Assert.True(item.Snippet!.Length <= 241);
            Assert.DoesNotContain(item.Snippet, character => char.IsControl(character));
        }
    }

    // ---- Steps 52 and 53: the structured query and the content term, against the real index ---------------------------

    private static readonly string[] CommonExtensions = [".png", ".jpg", ".jpeg", ".pdf", ".txt", ".docx", ".cs", ".json", ".md", ".xlsx"];

    [Fact]
    public async Task EveryOrderAndEveryCriterionIsAcceptedByTheRealProvider()
    {
        var everything = new FileSearchQuery(Word)
        {
            Filename = Word,
            Extensions = CommonExtensions,
            Modified = new DateRange(new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero), DateTimeOffset.UtcNow.AddDays(1)),
            Created = new DateRange(To: DateTimeOffset.UtcNow.AddDays(1)),
            Size = new SizeRange(0, long.MaxValue),
            MaxResultsPerType = 3,
        };

        foreach (var order in Enum.GetValues<FileSearchOrder>())
        {
            if (await TryAsync(Service(), everything with { Order = order }) is null)
            {
                return;
            }
        }

        foreach (var kind in Enum.GetValues<FileKind>())
        {
            await TryAsync(Service(), new FileSearchQuery { Kind = kind, Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 3 });
        }
    }

    [Fact]
    public async Task TheLatestFilesOfSomeTypesComeNewestFirstAndOldestFirstAndKeepToTheLimit()
    {
        var newest = await TryAsync(
            Service(),
            new FileSearchQuery { Extensions = CommonExtensions, Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 5 });
        if (newest is null)
        {
            return;
        }

        Assert.True(newest.Count <= 5);
        Assert.All(newest, item => Assert.Contains(item.Extension, CommonExtensions));
        Assert.Equal(newest.OrderByDescending(item => item.ModifiedAt).Select(item => item.Path), newest.Select(item => item.Path));

        var oldest = await Service().SearchAsync(
            new FileSearchQuery { Extensions = CommonExtensions, Order = FileSearchOrder.ModifiedAscending, MaxResultsPerType = 5 });
        Assert.Equal(oldest.OrderBy(item => item.ModifiedAt).Select(item => item.Path), oldest.Select(item => item.Path));
        if (newest.Count > 0 && oldest.Count > 0)
        {
            Assert.True(oldest[0].ModifiedAt <= newest[0].ModifiedAt);
        }

        // The same request gives the same list.
        var again = await Service().SearchAsync(
            new FileSearchQuery { Extensions = CommonExtensions, Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 5 });
        Assert.Equal(newest.Select(item => item.Path), again.Select(item => item.Path));
    }

    [Fact]
    public async Task ARangeOfDatesHoldsExactlyTheSecondsAskedForAndSoDoesEachEnd()
    {
        var latest = await TryAsync(
            Service(),
            new FileSearchQuery { Extensions = CommonExtensions, Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 1 });
        if (latest is not { Count: 1 })
        {
            return;
        }

        if (latest[0].ModifiedAt is not { } modified || latest[0].CreatedAt is not { } created)
        {
            return;
        }

        var within = await Service().SearchAsync(new FileSearchQuery
        {
            Extensions = CommonExtensions,
            Modified = new DateRange(modified, modified.AddSeconds(1)),
            MaxResultsPerType = 50,
        });
        Assert.Contains(latest[0].Path, within.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        Assert.All(within, item =>
        {
            Assert.True(item.ModifiedAt >= modified);
            Assert.True(item.ModifiedAt < modified.AddSeconds(1));
        });

        var after = await Service().SearchAsync(new FileSearchQuery
        {
            Extensions = CommonExtensions,
            Modified = new DateRange(modified.AddSeconds(1), null),
            MaxResultsPerType = 50,
        });
        Assert.DoesNotContain(latest[0].Path, after.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);

        var createdWithin = await Service().SearchAsync(new FileSearchQuery
        {
            Extensions = CommonExtensions,
            Created = new DateRange(created, created.AddSeconds(1)),
            MaxResultsPerType = 50,
        });
        Assert.Contains(latest[0].Path, createdWithin.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        Assert.All(createdWithin, item => Assert.Equal(created, item.CreatedAt));
    }

    [Fact]
    public async Task ASizeRangeHoldsOnlyFilesInsideIt()
    {
        var results = await TryAsync(
            Service(),
            new FileSearchQuery { Extensions = CommonExtensions, Size = new SizeRange(10_000, 200_000), MaxResultsPerType = 30 });
        if (results is null)
        {
            return;
        }

        Assert.All(results, item =>
        {
            Assert.NotNull(item.SizeBytes);
            Assert.InRange(item.SizeBytes.Value, 10_000, 200_000);
            Assert.Equal(SearchResultItemType.File, item.Type);
        });
    }

    [Fact]
    public async Task AFolderScopeHoldsOnlyItsOwnFilesAndTheRecursiveOneItsSubfolders()
    {
        var sample = await TryAsync(
            Service(),
            new FileSearchQuery { Extensions = CommonExtensions, Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 20 });
        var start = sample?.FirstOrDefault(item => Path.GetDirectoryName(Path.GetDirectoryName(item.Path)) is { Length: > 3 });
        if (start is null)
        {
            return;
        }

        var folder = Path.GetDirectoryName(start.Path)!;
        var parent = Path.GetDirectoryName(folder)!;

        // Found by its own name: what is escaped in a name (a bracket, a percent sign) is matched as it is.
        var direct = await Service().SearchAsync(new FileSearchQuery
        {
            Filename = start.DisplayName,
            Folder = folder,
            IncludeSubfolders = false,
            Types = [SearchResultItemType.File],
            MaxResultsPerType = 50,
        });
        Assert.Contains(start.Path, direct.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        Assert.All(direct, item => Assert.Equal(folder, Path.GetDirectoryName(item.Path), ignoreCase: true));

        var nested = await Service().SearchAsync(new FileSearchQuery
        {
            Filename = start.DisplayName,
            Folder = parent,
            Types = [SearchResultItemType.File],
            MaxResultsPerType = 50,
        });
        Assert.Contains(start.Path, nested.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
        Assert.All(nested, item => Assert.StartsWith(parent, item.Path, StringComparison.OrdinalIgnoreCase));

        // ...and the folder's parent, non-recursive, holds no file of the subfolder.
        var shallow = await Service().SearchAsync(new FileSearchQuery
        {
            Filename = start.DisplayName,
            Folder = parent,
            IncludeSubfolders = false,
            Types = [SearchResultItemType.File],
            MaxResultsPerType = 50,
        });
        Assert.DoesNotContain(start.Path, shallow.Select(item => item.Path), StringComparer.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("it's")]
    [InlineData("100%")]
    [InlineData("a_b")]
    [InlineData("[draft")]
    [InlineData("x'; DROP TABLE SystemIndex; --")]
    [InlineData("\"unclosed phrase")]
    [InlineData("*")]
    [InlineData("a* OR b*")]
    [InlineData("NEAR(a, b)")]
    [InlineData("(((")]
    [InlineData("C:\\Users\\*.txt")]
    [InlineData("üñïçødé 日本語 😀")]
    [InlineData("AND")]
    [InlineData("the")]
    [InlineData("-not")]
    public async Task HostileOrOddTextIsJustTextInEveryFieldOfTheStructuredQuery(string text)
    {
        FileSearchQuery[] queries =
        [
            new() { Filename = text, MaxResultsPerType = 3 },
            new() { ContentTerm = text, MaxResultsPerType = 3 },
            new() { Filename = text, ContentTerm = text, Extensions = [text, ".txt"], MaxResultsPerType = 3 },
            new() { Folder = @"C:\Users\" + text, MaxResultsPerType = 3 },
            new() { Folder = @"C:\Users\" + text, IncludeSubfolders = false, ContentTerm = "test", MaxResultsPerType = 3 },
        ];

        foreach (var query in queries)
        {
            try
            {
                await Service().SearchWithCapabilitiesAsync(query);
            }
            catch (FileSearchException exception) when (exception.Failure == FileSearchFailure.IndexUnavailable)
            {
                return;
            }
        }
    }

    [Fact]
    public async Task ContentSearchFindsPathsAndPropertiesOnlyAndFlagsWhatTheIndexCannotRead()
    {
        // Real files, in a folder made for the test in the Documents folder (indexed on a default Windows): what a content
        // search finds is decided by what the index has read of them, so this waits until it has caught up.
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrEmpty(documents) || !Directory.Exists(documents))
        {
            return;
        }

        var token = "zx" + Guid.NewGuid().ToString("N")[..12];
        var root = Path.Combine(documents, "assistant-search-test-" + token);
        var odd = Path.Combine(root, "it's #1 100% [x] & é");
        var empty = Path.Combine(Path.GetTempPath(), "assistant-search-empty-" + token);
        try
        {
            Directory.CreateDirectory(odd);
            Directory.CreateDirectory(empty);
            var older = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
            Write(Path.Combine(root, "notes-a.txt"), $"the {token} quarterly report is here", older);
            Write(Path.Combine(root, "notes-b.txt"), $"another {token} report of the quarter", older.AddDays(20));
            Write(Path.Combine(root, "notes-c.zzqqxx"), $"the {token} quarterly report is here", older.AddDays(10));
            Write(Path.Combine(odd, "odd.txt"), $"the {token} quarterly report in an odd folder", older.AddDays(30));

            var byName = new FileSearchQuery { Filename = "notes-", Folder = root, Types = [SearchResultItemType.File] };
            if (!await WaitForAsync(async () => (await Service().SearchAsync(byName)).Count == 3))
            {
                return;
            }

            // A phrase inside a file is found; a name that has it is not enough, and what is returned has no excerpt.
            var phrase = $"{token} quarterly report";
            if (!await WaitForAsync(async () => (await Service().SearchAsync(new FileSearchQuery { ContentTerm = phrase, Folder = root })).Count >= 2))
            {
                return;
            }

            var contents = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = phrase, Folder = root });
            Assert.Equal(
                ["notes-a.txt", "odd.txt"],
                contents.Items.Select(item => item.DisplayName).Order(StringComparer.OrdinalIgnoreCase));
            Assert.All(contents.Items, item =>
            {
                Assert.Null(item.Snippet);
                Assert.NotNull(item.SizeBytes);
                Assert.NotNull(item.ModifiedAt);
                Assert.Equal(".txt", item.Extension);
            });
            Assert.Equal(ContentSearchSupport.Available, contents.ContentSearch.Support);

            // Words in any order are not a phrase.
            Assert.Empty(await Service().SearchAsync(new FileSearchQuery { ContentTerm = "report quarterly " + token, Folder = root }));

            // The awkward folder is a scope like any other.
            var inOdd = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = phrase, Folder = odd, IncludeSubfolders = false });
            Assert.Equal(["odd.txt"], inOdd.Items.Select(item => item.DisplayName));

            // A type Windows has no filter for is flagged, and its text is not found.
            var unreadable = await Service().SearchWithCapabilitiesAsync(
                new FileSearchQuery { ContentTerm = phrase, Folder = root, Extensions = [".zzqqxx"] });
            Assert.Empty(unreadable.Items);
            Assert.Equal(ContentSearchSupport.Unavailable, unreadable.ContentSearch.Support);
            Assert.Equal(ContentSearchLimits.FileTypeNotContentIndexed, unreadable.ContentSearch.Limits);
            Assert.Equal([".zzqqxx"], unreadable.ContentSearch.UnsupportedExtensions);

            var mixed = await Service().SearchWithCapabilitiesAsync(
                new FileSearchQuery { ContentTerm = phrase, Folder = root, Extensions = [".zzqqxx", ".txt"] });
            Assert.Equal(ContentSearchSupport.Partial, mixed.ContentSearch.Support);
            Assert.Equal(["notes-a.txt", "odd.txt"], mixed.Items.Select(item => item.DisplayName).Order(StringComparer.OrdinalIgnoreCase));

            // A folder the index holds nothing under.
            var nowhere = await Service().SearchWithCapabilitiesAsync(new FileSearchQuery { ContentTerm = phrase, Folder = empty });
            Assert.Empty(nowhere.Items);
            Assert.Equal(ContentSearchSupport.Unavailable, nowhere.ContentSearch.Support);
            Assert.Equal(ContentSearchLimits.LocationNotIndexed, nowhere.ContentSearch.Limits);

            // Order, dates and sizes on files whose times were set.
            var files = new FileSearchQuery { Filename = "notes-", Folder = root, Extensions = [".txt", ".zzqqxx"] };

            // The times were set after the files were written, and the index may have read the files in between.
            await WaitForAsync(async () => (await Service().SearchAsync(files with { Order = FileSearchOrder.ModifiedDescending }))
                .Select(item => item.DisplayName).SequenceEqual(["notes-b.txt", "notes-c.zzqqxx", "notes-a.txt"]));
            Assert.Equal(
                ["notes-b.txt", "notes-c.zzqqxx", "notes-a.txt"],
                (await Service().SearchAsync(files with { Order = FileSearchOrder.ModifiedDescending })).Select(item => item.DisplayName));
            Assert.Equal(
                ["notes-a.txt", "notes-c.zzqqxx", "notes-b.txt"],
                (await Service().SearchAsync(files with { Order = FileSearchOrder.ModifiedAscending })).Select(item => item.DisplayName));
            Assert.Equal(
                ["notes-c.zzqqxx"],
                (await Service().SearchAsync(files with { Modified = new DateRange(older.AddDays(5), older.AddDays(15)) })).Select(item => item.DisplayName));
            Assert.Equal(
                ["notes-b.txt"],
                (await Service().SearchAsync(files with { Order = FileSearchOrder.ModifiedDescending, MaxResultsPerType = 1 })).Select(item => item.DisplayName));
            Assert.Equal(
                ["notes-a.txt", "notes-b.txt", "notes-c.zzqqxx"],
                (await Service().SearchAsync(files with { Order = FileSearchOrder.NameAscending })).Select(item => item.DisplayName));
            Assert.Equal(
                ["notes-c.zzqqxx", "notes-b.txt", "notes-a.txt"],
                (await Service().SearchAsync(files with { Order = FileSearchOrder.NameDescending })).Select(item => item.DisplayName));        }
        catch (FileSearchException exception) when (exception.Failure == FileSearchFailure.IndexUnavailable)
        {
            return;
        }
        finally
        {
            TryDelete(root);
            TryDelete(empty);
        }
    }

    private static void Write(string path, string text, DateTime modifiedUtc)
    {
        File.WriteAllText(path, text);
        File.SetLastWriteTimeUtc(path, modifiedUtc);
    }

    private static async Task<bool> WaitForAsync(Func<Task<bool>> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            if (await condition())
            {
                return true;
            }

            await Task.Delay(500);
        }

        return false;
    }

    private static void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover test folder is harmless.
        }
    }
}

/// <summary>The OLE DB client's own failures, against the real provider.</summary>
public sealed class OleDbClientTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

    private static async Task<bool> IsAvailableAsync()
    {
        try
        {
            await new Assistant.Search.Index.OleDbSearchIndexClient(Generous)
                .QueryAsync("SELECT TOP 1 System.ItemUrl FROM SystemIndex WHERE SCOPE='file:'", CancellationToken.None);
            return true;
        }
        catch (FileSearchException)
        {
            return false;
        }
    }

    [Fact]
    public async Task ABadQueryIsAQueryFailureThatCarriesOnlyTheProvidersCode()
    {
        if (!await IsAvailableAsync())
        {
            return;
        }

        var secret = "secret-table-name";
        var thrown = await Assert.ThrowsAsync<FileSearchException>(() =>
            new Assistant.Search.Index.OleDbSearchIndexClient(Generous)
                .QueryAsync($"SELECT System.NoSuchProperty{secret} FROM SystemIndex", CancellationToken.None));

        Assert.Equal(FileSearchFailure.QueryFailed, thrown.Failure);
        Assert.NotNull(thrown.ProviderErrorCode);
        Assert.Null(thrown.InnerException);
        Assert.DoesNotContain(secret, thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASlowQueryTimesOut()
    {
        if (!await IsAvailableAsync())
        {
            return;
        }

        var thrown = await Assert.ThrowsAsync<FileSearchException>(() =>
            new Assistant.Search.Index.OleDbSearchIndexClient(TimeSpan.FromMilliseconds(1))
                .QueryAsync(
                    "SELECT System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%e%' ORDER BY System.DateModified DESC",
                    CancellationToken.None));

        Assert.Equal(FileSearchFailure.TimedOut, thrown.Failure);
    }

    [Fact]
    public async Task CancellingWhileTheProviderWorksThrowsCancellationAtOnce()
    {
        if (!await IsAvailableAsync())
        {
            return;
        }

        using var source = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
        var client = new Assistant.Search.Index.OleDbSearchIndexClient(Generous);
        var started = System.Diagnostics.Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.QueryAsync(
            "SELECT System.ItemPathDisplay FROM SystemIndex WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%e%' ORDER BY System.DateModified DESC",
            source.Token));

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AProviderThatIsNotThereMeansTheIndexIsUnavailable()
    {
        var client = new Assistant.Search.Index.OleDbSearchIndexClient(Generous, "Provider=No.Such.Provider.1");

        var thrown = await Assert.ThrowsAsync<FileSearchException>(
            () => client.QueryAsync("SELECT System.ItemUrl FROM SystemIndex", CancellationToken.None));

        Assert.Equal(FileSearchFailure.IndexUnavailable, thrown.Failure);
    }

    [Fact]
    public async Task AQueryThatReturnsNothingReturnsAnEmptyList()
    {
        if (!await IsAvailableAsync())
        {
            return;
        }

        var rows = await new Assistant.Search.Index.OleDbSearchIndexClient(Generous).QueryAsync(
            "SELECT System.ItemUrl FROM SystemIndex WHERE SCOPE='file:' AND System.ItemNameDisplay LIKE '%zzqqxx-no-such-file-9187%'",
            CancellationToken.None);

        Assert.Empty(rows);
    }
}
