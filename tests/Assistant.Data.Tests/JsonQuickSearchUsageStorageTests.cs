using Assistant.Core.QuickSearch;
using Assistant.Data.QuickSearch;
using Xunit;

namespace Assistant.Data.Tests;

/// <summary>What the bar remembers of what was run (PROJECT_SPEC §3.5): one small file of salted hashes in the cache folder, never a name.</summary>
public sealed class JsonQuickSearchUsageStorageTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "assistant-usage-" + Guid.NewGuid().ToString("N"));

    private string File_ => Path.Combine(_folder, "cache", "quick-search-usage.json");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A temporary folder that stays is not a failure of the test.
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public void NothingKeptIsNoUsage()
    {
        Assert.Null(new JsonQuickSearchUsageStorage(File_).Load());
    }

    [Fact]
    public void WhatIsSavedIsLoadedAgainAndTheFolderIsMadeOnTheFirstWrite()
    {
        var storage = new JsonQuickSearchUsageStorage(File_);
        var snapshot = new QuickSearchUsageSnapshot(
            [1, 2, 3, 4], new Dictionary<string, QuickSearchUsageEntry> { ["abc"] = new(3, Now), ["def"] = new(1, Now.AddDays(-2)) });

        storage.Save(snapshot);
        var loaded = storage.Load();

        Assert.NotNull(loaded);
        Assert.Equal<byte>([1, 2, 3, 4], loaded.Salt);
        Assert.Equal(new QuickSearchUsageEntry(3, Now), loaded.Entries["abc"]);
        Assert.Equal(new QuickSearchUsageEntry(1, Now.AddDays(-2)), loaded.Entries["def"]);
        Assert.False(System.IO.File.Exists(File_ + ".tmp"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"Version":2,"Salt":"AQID","Entries":[]}""")]
    [InlineData("""{"Version":1,"Salt":"","Entries":[]}""")]
    [InlineData("""{"Version":1,"Salt":"AQID"}""")]
    [InlineData("""{"Version":1,"Salt":"%%%","Entries":[]}""")]
    public void AFileThatIsDamagedOrFromAnotherVersionIsNoUsage(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        System.IO.File.WriteAllText(File_, content);

        Assert.Null(new JsonQuickSearchUsageStorage(File_).Load());
    }

    [Fact]
    public void AnEntryWithoutAHashOrAUseIsLeftOut()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
        System.IO.File.WriteAllText(
            File_,
            """{"Version":1,"Salt":"AQID","Entries":[{"Hash":"","Uses":2,"LastUsed":"2026-10-02T09:00:00+00:00"},{"Hash":"x","Uses":0,"LastUsed":"2026-10-02T09:00:00+00:00"},{"Hash":"y","Uses":2,"LastUsed":"2026-10-02T09:00:00+00:00"}]}""");

        var loaded = new JsonQuickSearchUsageStorage(File_).Load();

        Assert.Equal(["y"], loaded!.Entries.Keys);
    }

    [Fact]
    public void AFileThatCannotBeWrittenIsOnlyNotKept()
    {
        // The path's folder is a file, so nothing can be made there.
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocker");
        System.IO.File.WriteAllText(blocker, "x");
        var storage = new JsonQuickSearchUsageStorage(Path.Combine(blocker, "usage.json"));

        storage.Save(new QuickSearchUsageSnapshot([1], new Dictionary<string, QuickSearchUsageEntry>()));

        Assert.Null(storage.Load());
    }

    [Fact]
    public void WhatTheUserRanIsKeptOnlyAsHashesAndComesBackAfterARestart()
    {
        const string Private = @"C:\Users\someone\Documents\tax-return-2025.pdf";
        var storage = new JsonQuickSearchUsageStorage(File_);
        var usage = new QuickSearchUsage(new FixedClock(Now), storage);

        usage.RecordUse(Private);
        usage.RecordUse(Private);
        var text = System.IO.File.ReadAllText(File_);

        Assert.DoesNotContain("tax-return", text, StringComparison.Ordinal);
        Assert.DoesNotContain("someone", text, StringComparison.Ordinal);
        var restarted = new QuickSearchUsage(new FixedClock(Now.AddDays(1)), new JsonQuickSearchUsageStorage(File_));
        Assert.Equal(new QuickSearchUsageEntry(2, Now), restarted.Find(Private));
        Assert.Null(restarted.Find(Private + "x"));

        restarted.Clear();
        Assert.Null(new QuickSearchUsage(storage: new JsonQuickSearchUsageStorage(File_)).Find(Private));
    }
}
