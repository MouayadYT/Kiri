using Assistant.Core.QuickSearch;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class QuickSearchRankingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 9, 0, 0, TimeSpan.Zero);

    private static QuickSearchResult App(string title, string id = "", double relevance = 0, string[]? keywords = null,
        string provider = "applications") =>
        QuickResults.Make(id.Length > 0 ? id : "app:" + title, title, QuickSearchResultType.Applications, provider, relevance, keywords);

    private static QuickSearchResult File(string title, string id = "", double relevance = 0) =>
        QuickResults.Make(id.Length > 0 ? id : "file:" + title, title, QuickSearchResultType.Files, "files", relevance);

    private static QuickSearchRanker Ranker(IQuickSearchUsage? usage = null) => new(
    [
        new ScriptedProvider("applications", QuickSearchResultType.Applications, priority: 100),
        new ScriptedProvider("files", QuickSearchResultType.Files, priority: 60),
        new ScriptedProvider("actions", QuickSearchResultType.Actions, priority: 80),
        new ScriptedProvider("clipboard", QuickSearchResultType.Clipboard, priority: 40),
    ], usage);

    [Fact]
    public void TheUsersResultsInEachGroupCapsEveryGroupWhileAllAreListedAndNeverANarrowedList()
    {
        var apps = Enumerable.Range(1, 9).Select(index => App("Note app " + index)).ToArray();
        var files = Enumerable.Range(1, 9).Select(index => File("Note file " + index)).ToArray();
        QuickSearchResult[] results = [.. apps, .. files];

        var capped = Ranker().Rank("note", results, Now, new QuickSearchRankingOptions { GlanceLimit = 2, TopHitMinimum = QuickSearchMatchKind.Exact });
        var normal = Ranker().Rank("note", results, Now, new QuickSearchRankingOptions { TopHitMinimum = QuickSearchMatchKind.Exact });
        var narrowed = Ranker().Rank("note", results, Now, new QuickSearchRankingOptions { Scope = QuickSearchResultType.Files, GlanceLimit = 2 });

        Assert.All(capped.Groups, group => Assert.Equal(2, group.Results.Count));
        Assert.Equal(QuickSearchGroups.GlanceLimit(QuickSearchResultType.Applications), normal.Groups.First().Results.Count);
        Assert.Equal(9, narrowed.Groups.Single().Results.Count);
    }

    [Theory]
    [InlineData("brave", "Brave", QuickSearchMatchKind.Exact)]
    [InlineData("BRAVE", "brave", QuickSearchMatchKind.Exact)]
    [InlineData("  brave  ", "Brave", QuickSearchMatchKind.Exact)]
    [InlineData("brave browser", "Brave - Browser", QuickSearchMatchKind.Exact)]
    [InlineData("cafe", "Caf\u00e9", QuickSearchMatchKind.Exact)]
    [InlineData("brave", "Brave Browser", QuickSearchMatchKind.Prefix)]
    [InlineData("brave br", "Brave Browser", QuickSearchMatchKind.Prefix)]
    [InlineData("studio code", "Visual Studio Code", QuickSearchMatchKind.Tokens)]
    [InlineData("code studio", "Visual Studio Code", QuickSearchMatchKind.Tokens)]
    [InlineData("vis st", "Visual Studio Code", QuickSearchMatchKind.Tokens)]
    [InlineData("vsc", "Visual Studio Code", QuickSearchMatchKind.Initials)]
    [InlineData("vs", "Visual Studio Code", QuickSearchMatchKind.Initials)]
    [InlineData("hrome", "Google Chrome", QuickSearchMatchKind.Contains)]
    [InlineData("oog rom", "Google Chrome", QuickSearchMatchKind.Contains)]
    [InlineData("zzz", "Google Chrome", QuickSearchMatchKind.None)]
    [InlineData("v", "Visual Studio Code", QuickSearchMatchKind.Prefix)]
    public void HowATitleMatchesWhatWasTyped(string typed, string title, QuickSearchMatchKind expected)
    {
        var match = QuickSearchMatch.Evaluate(typed, App(title));

        Assert.Equal(expected, match.Kind);
        Assert.False(match.ViaKeyword);
    }

    [Fact]
    public void AWordCannotBeUsedTwiceAndAnEmptyQueryMatchesNothing()
    {
        // "co co" needs two words beginning "co"; "Visual Studio Code" has only one, so it only contains them.
        Assert.Equal(QuickSearchMatchKind.Contains, QuickSearchMatch.Evaluate("co co", App("Visual Studio Code")).Kind);
        Assert.Equal(QuickSearchMatchKind.None, QuickSearchMatch.Evaluate("", App("Anything")).Kind);
        Assert.Equal(QuickSearchMatchKind.None, QuickSearchMatch.Evaluate("   ", App("Anything")).Kind);
        Assert.Equal(QuickSearchMatchKind.None, QuickSearchMatch.Evaluate("!!!", App("Anything")).Kind);
        Assert.Equal(QuickSearchMatchKind.None, QuickSearchMatch.Evaluate(null, App("Anything")).Kind);
    }

    [Fact]
    public void AKeywordMatchesButNeverCountsForMoreThanAWordThatBeginsAWord()
    {
        var control = App("Control Panel", keywords: ["settings", "options"]);

        var viaKeyword = QuickSearchMatch.Evaluate("settings", control);
        Assert.Equal(new QuickSearchMatch(QuickSearchMatchKind.Tokens, ViaKeyword: true), viaKeyword);

        // A keyword is only looked at when it does better than the title.
        Assert.Equal(new QuickSearchMatch(QuickSearchMatchKind.Prefix), QuickSearchMatch.Evaluate("control", control));
        Assert.Equal(QuickSearchMatchKind.None, QuickSearchMatch.Evaluate("printer", control).Kind);
    }

    [Fact]
    public void BetterMatchesComeFirstWhateverTheProviderOrTheOrderTheyArrivedIn()
    {
        var results = new[]
        {
            App("Google Chrome"),                 // a word begins with it
            File("my chrome notes.txt"),          // a word begins with it
            App("Chrome Beta"),                   // begins with it
            App("Chrome"),                        // is it
            App("Archrome", id: "app:contains"),  // only contains it
        };

        var ranking = Ranker().Rank("chrome", results, Now);

        Assert.Equal(
            ["app:Chrome", "app:Chrome Beta", "app:Google Chrome", "file:my chrome notes.txt", "app:contains"],
            ranking.Ordered.Select(ranked => ranked.Result.Id));
    }

    [Fact]
    public void WhatTheUserRunsComesFirstAmongEqualMatchesButNeverPassesABetterMatch()
    {
        var usage = new QuickSearchUsage(new FixedClock(Now));
        for (var i = 0; i < 40; i++)
        {
            usage.RecordUse("app:Notes Plus");
            usage.RecordUse("app:Zeta Notepad");
        }

        var ranker = Ranker(usage);
        var results = new[] { App("Notepad"), App("Zeta Notepad"), App("Notes Plus"), App("Notebook") };

        var ranking = ranker.Rank("note", results, Now);

        // "Notes Plus" begins with it and is used; "Notepad" and "Notebook" begin with it and are not, the shorter first; "Zeta Notepad"
        // is used a lot but only has a word that begins with it, which is a worse match.
        Assert.Equal(["app:Notes Plus", "app:Notepad", "app:Notebook", "app:Zeta Notepad"], ranking.Ordered.Select(ranked => ranked.Result.Id));
    }

    [Fact]
    public void UseCountsForHowOftenAndHowLately()
    {
        var usage = new FakeUsage();
        var ranker = Ranker(usage);

        Assert.Equal(0, ranker.UsageScore("never", Now));

        usage.Entries["once-long-ago"] = new QuickSearchUsageEntry(1, Now - TimeSpan.FromDays(90));
        usage.Entries["once-just-now"] = new QuickSearchUsageEntry(1, Now - TimeSpan.FromMinutes(5));
        usage.Entries["once-today"] = new QuickSearchUsageEntry(1, Now - TimeSpan.FromHours(5));
        usage.Entries["once-this-week"] = new QuickSearchUsageEntry(1, Now - TimeSpan.FromDays(3));
        usage.Entries["once-this-month"] = new QuickSearchUsageEntry(1, Now - TimeSpan.FromDays(20));
        usage.Entries["often-long-ago"] = new QuickSearchUsageEntry(1000, Now - TimeSpan.FromDays(90));
        usage.Entries["often-just-now"] = new QuickSearchUsageEntry(1000, Now);

        Assert.Equal(20, ranker.UsageScore("once-long-ago", Now));
        Assert.Equal(80, ranker.UsageScore("once-just-now", Now));
        Assert.Equal(65, ranker.UsageScore("once-today", Now));
        Assert.Equal(50, ranker.UsageScore("once-this-week", Now));
        Assert.Equal(35, ranker.UsageScore("once-this-month", Now));
        Assert.Equal(60, ranker.UsageScore("often-long-ago", Now));
        Assert.Equal(QuickSearchRanker.MaxUsageScore, ranker.UsageScore("often-just-now", Now));
    }

    [Fact]
    public void ProviderPriorityThenRelevanceThenNameSettleWhatTheRestCannot()
    {
        var results = new[]
        {
            File("report b.txt", id: "file:2", relevance: 0.1),
            File("report a.txt", id: "file:1", relevance: 0.9),
            App("report tool", id: "app:1"),
            QuickResults.Make("action:1", "report problem", QuickSearchResultType.Actions, "actions"),
        };

        var ranking = Ranker().Rank("report", results, Now);

        // All begin with the word, none is used: the application's provider outranks the actions', which outranks the files'. Of the two
        // files the provider's own relevance decides, and of two equally relevant ones the shorter name, then the alphabet, then the id.
        Assert.Equal(["app:1", "action:1", "file:1", "file:2"], ranking.Ordered.Select(ranked => ranked.Result.Id));

        var tie = Ranker().Rank("x", [File("xb", "file:b"), File("xa", "file:a"), File("xa", "file:0"), File("xaa", "file:c")], Now);
        Assert.Equal(["file:0", "file:a", "file:b", "file:c"], tie.Ordered.Select(ranked => ranked.Result.Id));
    }

    [Fact]
    public void TheSameResultsInAnyOrderRankTheSame()
    {
        var results = new[]
        {
            App("Notepad"), App("Notes"), App("Notepad++", relevance: 0.5), File("notes.txt"), File("note.md"),
            QuickResults.Make("action:n", "New note", QuickSearchResultType.Actions, "actions"),
            QuickResults.Make("clip:1", "note to self", QuickSearchResultType.Clipboard, "clipboard"),
            App("Sticky Notes", keywords: ["note"]),
        };
        var expected = Ranker().Rank("note", results, Now).Ordered.Select(ranked => ranked.Result.Id).ToArray();

        var random = new Random(7);
        for (var round = 0; round < 25; round++)
        {
            var shuffled = results.OrderBy(_ => random.Next()).ToArray();
            Assert.Equal(expected, Ranker().Rank("note", shuffled, Now).Ordered.Select(ranked => ranked.Result.Id));
        }
    }

    [Fact]
    public void AResultFoundTwiceCountsOnceAsItsBestCopy()
    {
        var results = new[]
        {
            App("Brave Browser", id: "app:brave", provider: "files"),
            App("Brave Browser", id: "app:brave", provider: "applications"),
        };

        var ranking = Ranker().Rank("brave", results, Now);

        var only = Assert.Single(ranking.Ordered);
        Assert.Equal("applications", only.Result.ProviderId);
    }

    [Fact]
    public void TheBestMatchLeadsAsTheTopHitAndIsNotRepeatedInItsGroup()
    {
        var results = new[]
        {
            App("Brave Browser"), App("Brave Beta"), App("Atlanta Braves", id: "app:braves"), File("brave notes.txt"),
            QuickResults.Make("action:1", "Open Brave settings", QuickSearchResultType.Actions, "actions"),
        };

        var ranking = Ranker().Rank("brave", results, Now);

        Assert.Equal("app:Brave Beta", ranking.TopHit!.Result.Id);
        Assert.Equal(
            [QuickSearchResultType.Applications, QuickSearchResultType.Files, QuickSearchResultType.Actions],
            ranking.Groups.Select(group => group.ResultType));
        Assert.DoesNotContain(ranking.Groups.SelectMany(group => group.Results), ranked => ranked.Result.Id == "app:Brave Beta");
        Assert.Equal(["app:Brave Browser", "app:braves"], ranking.Groups[0].Results.Select(ranked => ranked.Result.Id));
        Assert.Equal(["Applications", "Files", "Actions"], ranking.Groups.Select(group => group.Title));
        Assert.Equal(results.Length, ranking.Listed.Count);
    }

    [Fact]
    public void NoTopHitLeadsWhenNothingMatchesWellEnough()
    {
        var ranking = Ranker().Rank("hrome", [App("Google Chrome"), File("archrome.txt")], Now);

        Assert.Null(ranking.TopHit);
        Assert.Equal(2, ranking.Ordered.Count);

        // The least good match that leads is a choice of the caller's.
        var lenient = Ranker().Rank(
            "hrome", [App("Google Chrome")], Now, new QuickSearchRankingOptions { TopHitMinimum = QuickSearchMatchKind.Contains });
        Assert.NotNull(lenient.TopHit);
    }

    [Fact]
    public void ANameAnApplicationAlsoGoesByMatchesLikeItsTitleAndAnExactOneLeads()
    {
        var assistant = App("Assistant") with { Aliases = ["Kiri"] };
        var results = new[] { App("Kiri Notes"), File("kiri.txt"), assistant, App("Kirigami") };

        var ranking = Ranker().Rank("kiri", results, Now);

        Assert.Equal(QuickSearchMatchKind.Exact, QuickSearchMatch.Evaluate("kiri", assistant).Kind);
        Assert.False(QuickSearchMatch.Evaluate("kiri", assistant).ViaKeyword);
        Assert.Equal("app:Assistant", ranking.TopHit!.Result.Id);
        Assert.Equal("app:Assistant", ranking.Ordered[0].Result.Id);
    }

    [Theory]
    [InlineData("kir", QuickSearchMatchKind.Prefix)]
    [InlineData("ki", QuickSearchMatchKind.Prefix)]
    [InlineData("KIRI", QuickSearchMatchKind.Exact)]
    public void AnAliasIsFoundFromWhatItBeginsWithAndWithoutRegardToCase(string typed, QuickSearchMatchKind expected)
    {
        var assistant = App("Assistant") with { Aliases = ["Kiri"] };

        Assert.Equal(expected, QuickSearchMatch.Evaluate(typed, assistant).Kind);
    }

    [Fact]
    public void AnApplicationWithoutTheAliasIsNotFoundByIt()
    {
        Assert.Equal(QuickSearchMatchKind.None, QuickSearchMatch.Evaluate("kiri", App("Assistant")).Kind);
        Assert.Equal(QuickSearchMatchKind.None, QuickSearchMatch.Evaluate("kerry", App("Assistant") with { Aliases = ["Kiri"] }).Kind);
    }

    [Fact]
    public void GroupsComeInTheirOwnOrderAndShowAGlanceOfEach()
    {
        var results = Enumerable.Range(0, 12).Select(i => App($"zz app {i:00}"))
            .Concat(Enumerable.Range(0, 12).Select(i => File($"zz file {i:00}")))
            .Concat(Enumerable.Range(0, 12).Select(i => QuickResults.Make($"clip:{i}", $"zz clip {i:00}", QuickSearchResultType.Clipboard, "clipboard")))
            .Concat(Enumerable.Range(0, 12).Select(i => QuickResults.Make($"action:{i}", $"zz action {i:00}", QuickSearchResultType.Actions, "actions")))
            .Reverse()
            .ToArray();

        var ranking = Ranker().Rank("zz", results, Now);

        Assert.Equal(
            [QuickSearchResultType.Applications, QuickSearchResultType.Files, QuickSearchResultType.Actions, QuickSearchResultType.Clipboard],
            ranking.Groups.Select(group => group.ResultType));
        Assert.Equal([5, 8, 4, 4], ranking.Groups.Select(group => group.Results.Count));
        Assert.Equal(48, ranking.Ordered.Count);

        // The application that leads is not repeated in its group, which still shows five others.
        Assert.Equal(1 + 5 + 8 + 4 + 4, ranking.Listed.Count);
    }

    [Fact]
    public void ANarrowedListHasNoTopHitAndListsMoreOfOneGroup()
    {
        var results = Enumerable.Range(0, 30).Select(i => App($"zz app {i:00}"))
            .Concat(Enumerable.Range(0, 5).Select(i => File($"zz file {i:00}")))
            .ToArray();

        var ranking = Ranker().Rank("zz", results, Now, new QuickSearchRankingOptions { Scope = QuickSearchResultType.Applications });

        Assert.Null(ranking.TopHit);
        var group = Assert.Single(ranking.Groups);
        Assert.Equal(QuickSearchResultType.Applications, group.ResultType);
        Assert.Equal(QuickSearchGroups.ScopedLimit, group.Results.Count);
        Assert.Equal(30, ranking.Ordered.Count);
    }

    [Fact]
    public void ABrowsedGroupIsInOrderOfUseThenPriorityThenName()
    {
        var usage = new QuickSearchUsage(new FixedClock(Now));
        usage.RecordUse("app:Mail");
        var ranking = Ranker(usage).Rank("", [App("Photos"), App("Mail"), App("Calendar")], Now, new QuickSearchRankingOptions { Scope = QuickSearchResultType.Applications });

        Assert.Equal(["app:Mail", "app:Calendar", "app:Photos"], ranking.Ordered.Select(ranked => ranked.Result.Id));
        Assert.Null(ranking.TopHit);
    }

    [Fact]
    public void ATypeNobodyHasPlacedYetComesLastWithItsOwnName()
    {
        var future = (QuickSearchResultType)9;
        var results = new[] { QuickResults.Make("x:1", "zz thing", future, "future"), App("zz app") };

        // Nothing here matches well enough to lead, so both are listed in their groups.
        var ranking = Ranker().Rank("zz", results, Now, new QuickSearchRankingOptions { TopHitMinimum = QuickSearchMatchKind.Exact });

        Assert.Equal([QuickSearchResultType.Applications, future], ranking.Groups.Select(group => group.ResultType));
        Assert.Equal(future.ToString(), ranking.Groups[1].Title);
    }

    [Fact]
    public void NothingFoundIsAnEmptyRanking()
    {
        var ranking = Ranker().Rank("anything", [], Now);

        Assert.Same(QuickSearchRanking.Empty, ranking);
        Assert.Empty(ranking.Listed);
    }

    [Fact]
    public void UsageIsKeptAsSaltedHashesAndForgetsTheOldestFirst()
    {
        var clock = new TestClock(Now);
        var storage = new MemoryUsageStorage();
        var usage = new QuickSearchUsage(clock, storage, capacity: 3);

        foreach (var id in new[] { "file:C:\\tax\\return.pdf", "app:brave", "app:mail" })
        {
            usage.RecordUse(id);
            clock.Advance(TimeSpan.FromMinutes(1));
        }

        usage.RecordUse("app:brave");
        clock.Advance(TimeSpan.FromMinutes(1));
        usage.RecordUse("app:photos");

        // Four ids were used and three are kept: the one used longest ago, the tax return, is gone.
        Assert.Equal(3, usage.Count);
        Assert.Null(usage.Find("file:C:\\tax\\return.pdf"));
        Assert.Equal(2, usage.Find("app:brave")!.Uses);

        // What is kept names nothing the user opened.
        var saved = Assert.IsType<QuickSearchUsageSnapshot>(storage.Saved);
        Assert.Equal(3, saved.Entries.Count);
        Assert.All(saved.Entries.Keys, key => Assert.Matches("^[0-9A-F]{32}$", key));
        Assert.DoesNotContain(saved.Entries.Keys, key => key.Contains("brave", StringComparison.OrdinalIgnoreCase));

        // The next run knows what this one did.
        var next = new QuickSearchUsage(clock, storage, capacity: 3);
        Assert.Equal(2, next.Find("app:brave")!.Uses);
        Assert.Null(next.Find("app:unknown"));

        next.Clear();
        Assert.Equal(0, next.Count);
        Assert.Empty(storage.Saved!.Entries);
        Assert.Equal(0, new QuickSearchUsage(clock, storage).Count);
    }

    [Fact]
    public void UsageCountsOnlyUpToALimitAndIgnoresEntriesItCannotUse()
    {
        var storage = new MemoryUsageStorage
        {
            Saved = new QuickSearchUsageSnapshot([1, 2, 3], new Dictionary<string, QuickSearchUsageEntry>
            {
                ["AA"] = new(5000, Now),
                ["BB"] = new(0, Now),
                [""] = new(3, Now),
            }),
        };

        var usage = new QuickSearchUsage(new FixedClock(Now), storage);

        Assert.Equal(1, usage.Count);
        Assert.Throws<ArgumentOutOfRangeException>(() => new QuickSearchUsage(null, null, 0));
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeUsage : IQuickSearchUsage
    {
        public Dictionary<string, QuickSearchUsageEntry> Entries { get; } = [];

        public QuickSearchUsageEntry? Find(string resultId) => Entries.GetValueOrDefault(resultId);

        public void RecordUse(string resultId) => throw new NotSupportedException();

        public void Clear() => Entries.Clear();
    }

    private sealed class MemoryUsageStorage : IQuickSearchUsageStorage
    {
        public QuickSearchUsageSnapshot? Saved { get; set; }

        public QuickSearchUsageSnapshot? Load() => Saved;

        public void Save(QuickSearchUsageSnapshot snapshot) => Saved = snapshot;
    }
}
