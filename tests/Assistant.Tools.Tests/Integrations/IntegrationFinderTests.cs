using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Step 106: the Integration Finder runs only when allowed, searches in a trust-aware order, ranks what it finds and caches it.</summary>
public sealed class IntegrationFinderTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static IntegrationFinder Finder(
        IEnumerable<IIntegrationDiscoverySource> sources,
        IRepositoryEnricher? enricher = null,
        ICandidateAssessor? assessor = null,
        IDiscoveryCache? cache = null,
        bool localOnly = false,
        IPermissionPolicy? permissions = null,
        TimeProvider? clock = null,
        IntegrationFinderOptions? options = null) =>
        new(
            sources,
            enricher ?? new FakeEnricher(),
            assessor,
            cache ?? new MemoryDiscoveryCache(),
            TestSettings.LocalOnly(localOnly),
            permissions ?? new FakePermissions(true),
            clock ?? new ManualTimeProvider(Now),
            options ?? new IntegrationFinderOptions(),
            NullLogger<IntegrationFinder>.Instance);

    private static IntegrationCandidate Todoist(
        string name,
        CandidateTrust trust = CandidateTrust.Community,
        CapabilityEvidence evidence = CapabilityEvidence.AppOnly,
        DateTimeOffset? activity = null,
        int? stars = null,
        string? description = null,
        IReadOnlyList<string>? tools = null,
        string? repository = null) =>
        Candidates.Make(name, trust, description ?? "A Todoist MCP server", repository, activity ?? Now.AddDays(-5), evidence, stars, tools: tools);

    private static FakeSource First(string id, params IntegrationCandidate[] candidates) => FakeSource.Returning(id, DiscoveryStage.First, candidates);

    private static FakeSource Later(string id, params IntegrationCandidate[] candidates) => FakeSource.Returning(id, DiscoveryStage.WhenNeeded, candidates);

    // ---- the two locks ----

    [Fact]
    public async Task WhileLocalOnlyIsOnNothingIsAskedAnywhere()
    {
        var source = First("github", Todoist("Doist/todoist-mcp"));
        var cache = new MemoryDiscoveryCache();

        var result = await Finder([source], cache: cache, localOnly: true).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Blocked, result.Status);
        Assert.Equal(DiscoveryBlock.LocalOnly, result.Block);
        Assert.Empty(result.Candidates);
        Assert.Equal(0, source.Asked);
        Assert.Equal(0, cache.Reads);
    }

    [Fact]
    public async Task WithTheWebPermissionOffNothingIsAskedAnywhere()
    {
        var source = First("github", Todoist("Doist/todoist-mcp"));
        var permissions = new DenyingPermissions(PermissionCapability.ExternalSearch);

        var result = await Finder([source], permissions: permissions).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Blocked, result.Status);
        Assert.Equal(DiscoveryBlock.PermissionOff, result.Block);
        Assert.Equal(0, source.Asked);
        Assert.Equal([PermissionCapability.ExternalSearch], permissions.Asked);
    }

    [Fact]
    public async Task AnAnswerKeptFromBeforeIsNotGivenWhileTheLocksAreClosed()
    {
        var cache = new MemoryDiscoveryCache();
        var open = Finder([First("github", Todoist("Doist/todoist-mcp"))], cache: cache);
        await open.FindAsync(DiscoveryFixtures.Todoist);
        Assert.NotEmpty(cache.Entries);

        var closed = await Finder([], cache: cache, localOnly: true).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Blocked, closed.Status);
    }

    // ---- searching ----

    [Fact]
    public async Task TheSourcesAreAskedForTheAppAndTheCapabilityAndNothingTheUserWrote()
    {
        DiscoveryQuery? seen = null;
        var source = new FakeSource("github", DiscoveryStage.First, (query, _) =>
        {
            seen = query;
            return Task.FromResult<IReadOnlyList<IntegrationCandidate>>([]);
        });

        // The need cannot hold the request, so neither can the query made from it.
        var need = IntegrationRequestReader.Instance.Read("Add 'call my dentist about the secret invoice' to Microsoft To Do")!;
        await Finder([source]).FindAsync(need);

        Assert.NotNull(seen);
        Assert.Equal("microsoft todo", seen.Text);
        Assert.Equal("task", seen.ObjectWord);
        var everything = seen.Text + seen.Term + seen.ObjectWord + string.Join(',', seen.Owners);
        Assert.DoesNotContain("dentist", everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invoice", everything, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WhatIsFoundIsRankedByTrustThenEvidenceThenUpkeepThenPopularity()
    {
        var candidates = new[]
        {
            Todoist("popular/todoist-mcp", stars: 5000),
            Todoist("tools/todoist-mcp", evidence: CapabilityEvidence.ToolListed, stars: 5),
            Todoist("official/todoist-mcp", CandidateTrust.ClaimsOfficial),
            Todoist("maker/todoist-mcp", CandidateTrust.VerifiedVendor),
            Todoist("stale/todoist-mcp", evidence: CapabilityEvidence.ToolListed, activity: Now.AddYears(-3), stars: 100),
            Todoist("fresh/todoist-mcp", evidence: CapabilityEvidence.ToolListed, activity: Now.AddDays(-3), stars: 50),
        };

        var result = await Finder([First("github", candidates)], options: new IntegrationFinderOptions { MaxCandidates = 6 }).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Found, result.Status);
        Assert.Equal(
            ["maker/todoist-mcp", "official/todoist-mcp", "fresh/todoist-mcp", "tools/todoist-mcp", "stale/todoist-mcp", "popular/todoist-mcp"],
            result.Candidates.Select(candidate => candidate.Name));
    }

    [Fact]
    public async Task OnlyTheBestFewAreKept()
    {
        var candidates = Enumerable.Range(1, 9).Select(number => Todoist($"owner{number}/todoist-mcp", stars: number)).ToArray();

        var result = await Finder([First("github", candidates)]).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(5, result.Candidates.Count);
        Assert.Equal("owner9/todoist-mcp", result.Candidates[0].Name);
    }

    [Fact]
    public async Task WhatIsNotAboutTheAppIsDroppedAndSoIsWhatIsArchived()
    {
        var result = await Finder([First(
            "github",
            Todoist("Doist/todoist-mcp"),
            Candidates.Make("public-apis/public-apis", description: "A collective list of free APIs"),
            Candidates.Make("someone/notion-mcp", description: "Notion for assistants"),
            Todoist("old/todoist-mcp") with { Archived = true })]).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(["Doist/todoist-mcp"], result.Candidates.Select(candidate => candidate.Name));
    }

    [Fact]
    public async Task TheSameProjectFoundTwiceIsOneCandidateThatKnowsWhereItWasFound()
    {
        var fromGitHub = Todoist("Doist/todoist-mcp", CandidateTrust.VerifiedVendor, repository: "https://github.com/Doist/todoist-mcp") with { Stars = 553, License = "MIT" };
        var fromRegistry = Todoist("io.github.Doist/todoist-mcp", CandidateTrust.VerifiedVendor, repository: "https://github.com/Doist/todoist-mcp") with
        {
            Packages = [new CandidatePackage(CandidateInstallMethod.Npm, "@doist/todoist-mcp", "13.4.0")],
            RequiredSecrets = ["TODOIST_API_KEY"],
            FoundIn = ["mcp-registry"],
        };

        var result = await Finder([First("github", fromGitHub), First("mcp-registry", fromRegistry)]).FindAsync(DiscoveryFixtures.Todoist);

        var merged = Assert.Single(result.Candidates);
        Assert.Equal("MIT", merged.License);
        Assert.Equal(553, merged.Stars);
        Assert.Equal(["TODOIST_API_KEY"], merged.RequiredSecrets);
        Assert.Contains(merged.Packages, package => package.Method == CandidateInstallMethod.Npm);
        Assert.Contains("mcp-registry", merged.FoundIn);
    }

    [Fact]
    public async Task APackageThatOnlyNamesTheMakersRepositoryIsNeverMergedIntoIt()
    {
        var real = Todoist("Doist/todoist-mcp", CandidateTrust.VerifiedVendor, repository: "https://github.com/Doist/todoist-mcp");
        var impostor = Todoist("todoist-mcp-impostor", CandidateTrust.Community, repository: "https://github.com/Doist/todoist-mcp");

        var result = await Finder([First("github", real), First("npm2", impostor)]).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(["Doist/todoist-mcp", "todoist-mcp-impostor"], result.Candidates.Select(candidate => candidate.Name));
        Assert.Equal(CandidateTrust.Community, result.Candidates[1].Trust);
    }

    // ---- staging ----

    [Fact]
    public async Task ThePackageRegistriesAreAskedOnlyWhenNothingAboutTheAppFitsOtherwise()
    {
        var npm = Later("npm", Todoist("@someone/todoist-mcp"));
        var fits = await Finder([First("github", Todoist("Doist/todoist-mcp", evidence: CapabilityEvidence.Described)), npm]).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(0, npm.Asked);
        Assert.Equal(["github"], fits.SourcesAnswered);

        var nothing = await Finder([First("github"), npm]).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(1, npm.Asked);
        Assert.Equal(["@someone/todoist-mcp"], nothing.Candidates.Select(candidate => candidate.Name));
        Assert.Equal(["github", "npm"], nothing.SourcesAnswered);
    }

    [Fact]
    public async Task ACandidateThatIsAboutTheAppButNotKnownToDoTheThingAlsoLetsThePackageRegistriesIn()
    {
        var npm = Later("npm", Todoist("@doist/todoist-mcp", CandidateTrust.VerifiedVendor, CapabilityEvidence.ToolListed));

        var result = await Finder([First("github", Todoist("fan/todoist-mcp")), npm]).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(1, npm.Asked);
        Assert.Equal("@doist/todoist-mcp", result.Candidates[0].Name);
    }

    [Fact]
    public async Task OnceTheMakersOwnIntegrationIsInASlowSourceIsNoLongerWaitedFor()
    {
        var slow = FakeSource.Hanging("mcp-registry", DiscoveryStage.First);
        var github = First("github", Todoist("Doist/todoist-mcp", CandidateTrust.VerifiedVendor));
        var timer = Stopwatch.StartNew();

        var result = await Finder([slow, github]).FindAsync(DiscoveryFixtures.Todoist);

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), "The finder waited for the slow source.");
        Assert.Equal(DiscoveryStatus.Found, result.Status);
        Assert.Equal(["github"], result.SourcesAnswered);
        Assert.Empty(result.SourcesFailed);
    }

    [Fact]
    public async Task ASourceThatIsTooSlowIsRecordedAsFailedAndTheOthersAreUsed()
    {
        var slow = FakeSource.Hanging("mcp-registry", DiscoveryStage.First);
        var github = First("github", Todoist("fan/todoist-mcp"));
        var options = new IntegrationFinderOptions { SourceTimeout = TimeSpan.FromMilliseconds(100) };

        var result = await Finder([slow, github], options: options).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Found, result.Status);
        Assert.Equal(["github"], result.SourcesAnswered);
        Assert.Equal(["mcp-registry"], result.SourcesFailed);
    }

    [Fact]
    public async Task WhenNoSourceAnswersTheSearchFailsAndIsNotKept()
    {
        var cache = new MemoryDiscoveryCache();

        var result = await Finder(
            [FakeSource.Failing("github", DiscoveryStage.First), FakeSource.Failing("mcp-registry", DiscoveryStage.First), FakeSource.Failing("npm", DiscoveryStage.WhenNeeded)],
            cache: cache).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Empty(result.Candidates);
        Assert.Contains("github", result.SourcesFailed);
        Assert.Empty(result.SourcesAnswered);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public async Task ASearchNoPlaceAnsweredIsNeverKept()
    {
        var cache = new MemoryDiscoveryCache();

        // No source to ask is not an answer that nothing exists.
        var result = await Finder([], cache: cache).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.NothingPlausible, result.Status);
        Assert.Empty(result.SourcesAnswered);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public async Task AnswersWithNothingThatFitsAreNothingPlausibleAndAreKeptBriefly()
    {
        var cache = new MemoryDiscoveryCache();

        var result = await Finder([First("github", Candidates.Make("x/unrelated", description: "Something else"))], cache: cache).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.NothingPlausible, result.Status);
        Assert.Equal(["github"], result.SourcesAnswered);
        var kept = Assert.Single(cache.Entries);
        Assert.Equal("todoist|create:task", kept.Key);
        Assert.Equal(Now.AddHours(2), kept.Value.ExpiresAt);
    }

    [Fact]
    public async Task SomethingFoundIsKeptForADay()
    {
        var cache = new MemoryDiscoveryCache();

        await Finder([First("github", Todoist("Doist/todoist-mcp"))], cache: cache).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(Now.AddHours(24), Assert.Single(cache.Entries).Value.ExpiresAt);
    }

    [Fact]
    public async Task AskingAgainForTheSameAppAndCapabilityIsAnsweredFromTheCacheAndSendsNothing()
    {
        var clock = new ManualTimeProvider(Now);
        var source = First("github", Todoist("Doist/todoist-mcp"));
        var finder = Finder([source], cache: new MemoryDiscoveryCache(), clock: clock);

        var first = await finder.FindAsync(DiscoveryFixtures.Todoist);
        clock.Advance(TimeSpan.FromHours(3));
        var second = await finder.FindAsync(DiscoveryFixtures.Todoist);

        Assert.False(first.FromCache);
        Assert.True(second.FromCache);
        Assert.Equal(first.Candidates.Select(candidate => candidate.Name), second.Candidates.Select(candidate => candidate.Name));
        Assert.Equal(1, source.Asked);
    }

    [Fact]
    public async Task ACachedAnswerExpires()
    {
        var clock = new ManualTimeProvider(Now);
        var source = First("github", Todoist("Doist/todoist-mcp"));
        var finder = Finder([source], clock: clock);
        await finder.FindAsync(DiscoveryFixtures.Todoist);

        clock.Advance(TimeSpan.FromHours(25));
        var again = await finder.FindAsync(DiscoveryFixtures.Todoist);

        Assert.False(again.FromCache);
        Assert.Equal(2, source.Asked);
    }

    [Fact]
    public async Task AnotherCapabilityOfTheSameAppIsASeparateSearch()
    {
        var source = First("github", Todoist("Doist/todoist-mcp"));
        var finder = Finder([source]);
        await finder.FindAsync(DiscoveryFixtures.Todoist);

        await finder.FindAsync(DiscoveryFixtures.Todoist with { Capability = new IntegrationCapability(CapabilityAction.Read, "task") });

        Assert.Equal(2, source.Asked);
    }

    [Fact]
    public async Task WhatIsAlreadyInstalledIsNotOfferedAgainAndTheCacheIsLeftAlone()
    {
        var cache = new MemoryDiscoveryCache();
        var source = First("github", Todoist("Doist/todoist-mcp", repository: "https://github.com/Doist/todoist-mcp"), Todoist("fan/todoist-mcp"));

        var result = await Finder([source], cache: cache).FindAsync(DiscoveryFixtures.Todoist, ["https://github.com/Doist/todoist-mcp/"]);

        Assert.Equal(["fan/todoist-mcp"], result.Candidates.Select(candidate => candidate.Name));
        Assert.Empty(cache.Entries);
        Assert.Equal(0, cache.Reads);
    }

    // ---- reading the best candidates ----

    [Fact]
    public async Task OnlyTheBestFewHaveTheirRepositoryRead()
    {
        var enricher = new FakeEnricher(candidate => candidate with { Evidence = CapabilityEvidence.ToolListed });
        var candidates = Enumerable.Range(1, 6).Select(number => Todoist($"owner{number}/todoist-mcp", stars: number)).ToArray();

        var result = await Finder([First("github", candidates)], enricher).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(3, enricher.Enriched.Count);
        Assert.Equal(3, result.Candidates.Count(candidate => candidate.Evidence == CapabilityEvidence.ToolListed));
        Assert.Equal(["owner6/todoist-mcp", "owner5/todoist-mcp", "owner4/todoist-mcp"], enricher.Enriched.OrderDescending());
    }

    [Fact]
    public async Task ACandidateIsReadOnceEvenWhenTheSearchGoesOnToTheNextStage()
    {
        var enricher = new FakeEnricher();

        await Finder([First("github", Todoist("fan/todoist-mcp")), Later("npm", Todoist("@x/todoist-mcp"))], enricher).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(enricher.Enriched.Distinct().Count(), enricher.Enriched.Count);
    }

    [Fact]
    public async Task ACandidateTooSlowToReadIsKeptAsItWas()
    {
        var slow = new SlowEnricher();
        var options = new IntegrationFinderOptions { EnrichTimeout = TimeSpan.FromMilliseconds(100) };

        var result = await Finder([First("github", Todoist("Doist/todoist-mcp"))], slow, options: options).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(["Doist/todoist-mcp"], result.Candidates.Select(candidate => candidate.Name));
    }

    private sealed class SlowEnricher : IRepositoryEnricher
    {
        public async Task<IntegrationCandidate> EnrichAsync(IntegrationCandidate candidate, IntegrationCapability capability, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return candidate;
        }
    }

    // ---- the model's judgement ----

    [Fact]
    public async Task TheModelIsNotAskedWhenEveryCandidatesOwnToolsSettleIt()
    {
        var assessor = new FakeAssessor();

        await Finder([First("github", Todoist("Doist/todoist-mcp", evidence: CapabilityEvidence.ToolListed))], assessor: assessor).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(0, assessor.Asked);
    }

    [Fact]
    public async Task TheModelJudgesTheFewThatRemainAndOnlyReordersThem()
    {
        var assessor = new FakeAssessor(candidates => [.. candidates.Select(candidate =>
            candidate.Name.StartsWith("good", StringComparison.Ordinal) ? CandidateAssessment.Supports : CandidateAssessment.DoesNotSupport)]);
        var result = await Finder(
            [First("github", Todoist("bad/todoist-mcp", stars: 900), Todoist("good/todoist-mcp", stars: 1), Todoist("alsobad/todoist-mcp", stars: 500))], assessor: assessor)
            .FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(1, assessor.Asked);
        Assert.Equal("good/todoist-mcp", result.Candidates[0].Name);
        Assert.Equal(CandidateAssessment.Supports, result.Candidates[0].Assessment);
        Assert.Equal(3, result.Candidates.Count);
        Assert.All(result.Candidates.Skip(1), candidate => Assert.Equal(CandidateAssessment.DoesNotSupport, candidate.Assessment));
        Assert.Equal(["good/todoist-mcp", "bad/todoist-mcp", "alsobad/todoist-mcp"], result.Candidates.Select(candidate => candidate.Name));
    }

    [Fact]
    public async Task ASmallModelsDoubtDoesNotDemoteACandidateWhoseOwnToolsNameTheCapability()
    {
        var assessor = new FakeAssessor(candidates => [.. candidates.Select(_ => CandidateAssessment.DoesNotSupport)]);
        var result = await Finder(
            [First("github", Todoist("listed/todoist-mcp", evidence: CapabilityEvidence.ToolListed), Todoist("plain/todoist-mcp", stars: 10_000))], assessor: assessor)
            .FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal("listed/todoist-mcp", result.Candidates[0].Name);
        Assert.Equal(CandidateAssessment.NotJudged, result.Candidates[0].Assessment);
    }

    [Fact]
    public async Task TheModelNeverMakesTheMakersOwnIntegrationRankBelowAGuessWhenItIsNotSure()
    {
        var assessor = new FakeAssessor(candidates => [.. candidates.Select(_ => CandidateAssessment.NotJudged)]);
        var result = await Finder(
            [First("github", Todoist("fan/todoist-mcp", stars: 9000), Todoist("Doist/todoist-mcp", CandidateTrust.VerifiedVendor))], assessor: assessor)
            .FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal("Doist/todoist-mcp", result.Candidates[0].Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task AnAnswerThatCannotBeUsedLeavesTheOrderAsItWas(int kind)
    {
        ICandidateAssessor assessor = kind == 0
            ? new FakeAssessor(_ => null)
            : new FakeAssessor(_ => [CandidateAssessment.Supports]);

        var result = await Finder(
            [First("github", Todoist("a/todoist-mcp", stars: 9), Todoist("b/todoist-mcp", stars: 1))], assessor: assessor).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(["a/todoist-mcp", "b/todoist-mcp"], result.Candidates.Select(candidate => candidate.Name));
        Assert.All(result.Candidates, candidate => Assert.Equal(CandidateAssessment.NotJudged, candidate.Assessment));
    }

    [Fact]
    public async Task AModelThatThrowsLeavesTheSearchIntact()
    {
        var result = await Finder([First("github", Todoist("a/todoist-mcp"))], assessor: new ThrowingAssessor()).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Found, result.Status);
    }

    private sealed class ThrowingAssessor : ICandidateAssessor
    {
        public Task<IReadOnlyList<CandidateAssessment>?> AssessAsync(IntegrationNeed need, IReadOnlyList<IntegrationCandidate> candidates, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The model failed.");
    }

    [Fact]
    public async Task TheModelOnlySeesTheCandidatesThatMadeTheCut()
    {
        var assessor = new FakeAssessor();
        var candidates = Enumerable.Range(1, 9).Select(number => Todoist($"owner{number}/todoist-mcp", stars: number)).ToArray();

        await Finder([First("github", candidates)], assessor: assessor).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(5, assessor.Seen.Count);
    }

    // ---- stopping and limits ----

    [Fact]
    public async Task StoppingEndsTheSearch()
    {
        using var source = new CancellationTokenSource();
        var hanging = FakeSource.Hanging("github", DiscoveryStage.First);
        var task = Finder([hanging]).FindAsync(DiscoveryFixtures.Todoist, null, source.Token);
        await source.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ASearchThatRunsOutOfTimeFailsAndIsNotKept()
    {
        var cache = new MemoryDiscoveryCache();
        var options = new IntegrationFinderOptions { TotalTimeout = TimeSpan.FromMilliseconds(100), SourceTimeout = TimeSpan.FromMinutes(5) };

        var result = await Finder([FakeSource.Hanging("github", DiscoveryStage.First)], cache: cache, options: options).FindAsync(DiscoveryFixtures.Todoist);

        Assert.Equal(DiscoveryStatus.Failed, result.Status);
        Assert.Empty(cache.Entries);
    }

    [Fact]
    public async Task AnAppWhoseNameCannotBeSearchedWithIsNeverSearched()
    {
        var source = First("github", Todoist("a/todoist-mcp"));
        var need = new IntegrationNeed("日本語", "日本語", new IntegrationCapability(CapabilityAction.Create, null), IntegrationNeedSource.Stated);

        var result = await Finder([source]).FindAsync(need);

        Assert.Equal(DiscoveryStatus.NothingPlausible, result.Status);
        Assert.Equal(0, source.Asked);
    }

    [Fact]
    public async Task ALoggerThatSeesEverythingNeverSeesTheAppOrWhatWasFound()
    {
        var logger = new CapturingLoggerFactory();
        var finder = new IntegrationFinder(
            [First("github", Todoist("Doist/todoist-mcp", description: "secretdescription"))], new FakeEnricher(), null, new MemoryDiscoveryCache(), TestSettings.LocalOnly(false),
            new FakePermissions(true), new ManualTimeProvider(Now), new IntegrationFinderOptions(), logger.CreateLogger<IntegrationFinder>());

        await finder.FindAsync(DiscoveryFixtures.Todoist);

        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("todoist", StringComparison.OrdinalIgnoreCase) || line.Contains("secretdescription", StringComparison.Ordinal));
    }
}
