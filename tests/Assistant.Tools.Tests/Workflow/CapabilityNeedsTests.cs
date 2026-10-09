using Assistant.Core.Calendar;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Messaging;
using Assistant.Tools.Calendar;
using Assistant.Tools.Integrations;
using Assistant.Tools.Messaging;
using Assistant.Tools.Tests.Integrations;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Workflow;

/// <summary>
/// A request that names no app but asks for something an integration does ("check my calendar", "message my brother"; PROJECT_SPEC §4.8, step 116): how it is read, how it is resolved against
/// what is installed, and how a need that nothing serves goes through the same finder, review, offer and resume as a request for a named app, and only while it is switched on.
/// </summary>
public sealed class CapabilityNeedsTests
{
    private static string Phrases(string request) => string.Join(", ", CapabilityRequestReader.Read(request).Select(need => need.Capability.Phrase));

    // ---- The reader ----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheCanonicalRequestNeedsTheCalendarAndThenAMessage()
    {
        Assert.Equal("read event, send message", Phrases(WorkflowFixture.CanonicalRequest));
    }

    [Theory]
    [InlineData("What's on my calendar tomorrow?", "read event")]
    [InlineData("Check my calendar", "read event")]
    [InlineData("Show my agenda for Friday", "read event")]
    [InlineData("Look at my work calendar for next week", "read event")]
    [InlineData("Tell me what's on my calendar", "read event")]
    [InlineData("Can you check my calendar for exams?", "read event")]
    [InlineData("Message my brother that I'm late", "send message")]
    [InlineData("Text my mom happy birthday", "send message")]
    [InlineData("Tell my sister I'll call tomorrow", "send message")]
    [InlineData("Let my dad know I arrived", "send message")]
    [InlineData("Remind my brother about dinner", "send message")]
    [InlineData("Please send a message to my wife", "send message")]
    [InlineData("Message my brother and check my calendar", "send message, read event")]
    public void WhatARequestAsksOfAnIntegrationIsReadByFixedRules(string request, string expected)
    {
        Assert.Equal(expected, Phrases(request));
    }

    [Theory]
    [InlineData("Add lunch to my calendar")]
    [InlineData("Schedule a meeting on my calendar")]
    [InlineData("Cancel my calendar entry")]
    [InlineData("Check my calendar and add lunch on Friday")]
    [InlineData("Show my calendar and cancel the dentist")]
    [InlineData("How do I use my calendar?")]
    [InlineData("Is my brother coming?")]
    [InlineData("What does my brother like?")]
    [InlineData("Remind me to call my brother")]
    [InlineData("Tell me about my brother")]
    [InlineData("Message Omar")]
    [InlineData("Check the calendar")]
    [InlineData("What is the weather today?")]
    [InlineData("hello")]
    [InlineData("")]
    [InlineData(null)]
    public void WhatIsNotARequestForAnIntegrationIsReadAsNothing(string? request)
    {
        Assert.Empty(CapabilityRequestReader.Read(request));
    }

    [Fact]
    public void APastedTextIsNotARequest()
    {
        Assert.Empty(CapabilityRequestReader.Read("Check my calendar " + new string('x', IntegrationRequestReader.MaxRequestLength)));
    }

    [Fact]
    public void ANeedHoldsTwoWordsAndNothingTheUserWrote()
    {
        var calendar = IntegrationNeed.ForCalendar();
        var messaging = IntegrationNeed.ForMessaging();

        Assert.Equal(("calendar", "calendar", "read event"), (calendar.AppName, calendar.AppKey, calendar.Capability.Phrase));
        Assert.Equal(("messaging", "messaging", "send message"), (messaging.AppName, messaging.AppKey, messaging.Capability.Phrase));
        Assert.True(calendar.IsForAnyApp);
        Assert.Equal("calendar|read:event", calendar.CacheKey);
        Assert.False(DiscoveryFixtures.Todoist.IsForAnyApp);
        Assert.Equal("read event", CapabilityRequestReader.CalendarNeed(WorkflowFixture.CanonicalRequest)!.Capability.Phrase);
        Assert.Null(CapabilityRequestReader.CalendarNeed("Message my brother"));
    }

    // ---- The resolver --------------------------------------------------------------------------------------------------

    private static IntegrationResolver ResolverFor(ConnectedAppsFixture apps, IReadOnlyList<IAvailableIntegrationSource>? sources = null) =>
        new(
            apps.Integrations, apps.Manager, apps.Settings, IntegrationRequestReader.Instance, sources ?? [], apps.Permissions, apps.Clock, new IntegrationResolverOptions(),
            NullLogger<IntegrationResolver>.Instance);

    // The clock is the fixture's own (an hour after the tool names were read), not the system's: what the registry kept lasts a day, so a test on the real clock fails once the day has passed.
    private static ConnectedAppsFixture Apps(params InstalledIntegration[] integrations) =>
        new(
            integrations,
            clients: integration => integration.Id == "samplecalendar" ? WorkflowFixture.CalendarClient(WorkflowFixture.EventsJson) : WorkflowFixture.MessagesClient(WorkflowFixture.ChatsJson),
            clock: new AgentLoopConnectedAppsTests.Clock(WorkflowFixture.Start.AddHours(1)));

    [Fact]
    public async Task WithNothingInstalledANeedForAKindOfThingMayBeLookedFor()
    {
        await using var apps = Apps();

        var resolution = await ResolverFor(apps).ResolveAsync(IntegrationNeed.ForCalendar());

        Assert.Equal(IntegrationResolutionKind.NotInstalled, resolution.Kind);
        Assert.True(resolution.DiscoveryAllowed);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnInstalledIntegrationWithAToolForItServesTheNeedWhateverItIsCalled()
    {
        var renamed = WorkflowFixture.CalendarIntegration() with { Id = "gcal", Name = "Gcal" };
        await using var apps = Apps(renamed);

        var resolution = await ResolverFor(apps).ResolveAsync(IntegrationNeed.ForCalendar());

        Assert.Equal(IntegrationResolutionKind.UseInstalled, resolution.Kind);
        Assert.Equal("gcal", resolution.Integration!.Id);
        Assert.Contains("list_events", resolution.ToolNames);
        Assert.True(resolution.FromCache);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnIntegrationWithNoToolForTheNeedIsNotTheOneThatServesIt()
    {
        var notes = Sample.Remote("notes", "Notes") with { Capabilities = new IntegrationCapabilities { Tools = true, ToolNames = ["list_notes", "add_note"], RefreshedAt = WorkflowFixture.Start } };
        await using var apps = Apps(notes);

        var calendar = await ResolverFor(apps).ResolveAsync(IntegrationNeed.ForCalendar());
        var messaging = await ResolverFor(apps).ResolveAsync(IntegrationNeed.ForMessaging());

        Assert.Equal(IntegrationResolutionKind.NotInstalled, calendar.Kind);
        Assert.Equal(IntegrationResolutionKind.NotInstalled, messaging.Kind);
    }

    [Fact]
    public async Task TheMessagingNeedIsServedByAnAppWithATextSendingTool()
    {
        await using var apps = Apps(WorkflowFixture.MessagesIntegration());

        var resolution = await ResolverFor(apps).ResolveAsync(IntegrationNeed.ForMessaging());

        Assert.Equal(IntegrationResolutionKind.UseInstalled, resolution.Kind);
        Assert.Equal("send_message", resolution.ToolNames[0]);
    }

    [Fact]
    public async Task AnIntegrationThatIsForItButTurnedOffIsSaidSoAndNotLookedForAgain()
    {
        await using var apps = Apps(WorkflowFixture.CalendarIntegration() with { Enabled = false });

        var resolution = await ResolverFor(apps).ResolveAsync(IntegrationNeed.ForCalendar());

        Assert.Equal(IntegrationResolutionKind.InstalledNotUsable, resolution.Kind);
        Assert.Equal(InstalledProblem.Disabled, resolution.Problem);
        Assert.False(resolution.DiscoveryAllowed);
    }

    [Fact]
    public async Task AnIntegrationThatNeedsASignInIsSaidSoAndNotLookedForAgain()
    {
        var signedOut = WorkflowFixture.CalendarIntegration() with
        {
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.NeedsSignIn },
        };
        await using var apps = Apps(signedOut);

        var resolution = await ResolverFor(apps).ResolveAsync(IntegrationNeed.ForCalendar());

        Assert.Equal(InstalledProblem.NeedsSignIn, resolution.Problem);
        Assert.False(resolution.DiscoveryAllowed);
    }

    [Fact]
    public async Task ADeleteIsStillRefusedWhateverIsInstalled()
    {
        await using var apps = Apps(WorkflowFixture.CalendarIntegration());
        var delete = new IntegrationNeed("calendar", "calendar", new IntegrationCapability(CapabilityAction.Delete, "event"), IntegrationNeedSource.Capability);

        Assert.Equal(IntegrationResolutionKind.Refused, (await ResolverFor(apps).ResolveAsync(delete)).Kind);
    }

    // ---- The handler ---------------------------------------------------------------------------------------------------

    private sealed class RecordingFinder(IntegrationDiscoveryResult result) : IIntegrationFinder
    {
        public List<IntegrationNeed> Asked { get; } = [];

        public Task<IntegrationDiscoveryResult> FindAsync(IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default)
        {
            Asked.Add(need);
            return Task.FromResult(result);
        }
    }

    private sealed class AcceptingReviewer : ICandidateReviewer
    {
        public Task<CandidateReview> ReviewAsync(IntegrationCandidate candidate, IntegrationNeed need, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<CandidateReview>> ReviewAllAsync(IReadOnlyList<IntegrationCandidate> candidates, IntegrationNeed need, CancellationToken cancellationToken = default)
        {
            var source = new InstallSource
            {
                Kind = InstallSourceKind.Bundle,
                Identifier = "https://github.com/example/cal/releases/download/v1/x.mcpb",
                Version = "1.0.0",
                DownloadUrl = "https://github.com/example/cal/releases/download/v1/x.mcpb",
                Hash = new ContentHash("sha256", new string('a', 64)),
            };
            var accepted = new InstallCandidate
            {
                Id = "gcal",
                AppName = "Google Calendar",
                Name = "example/google-calendar-mcp",
                Source = source,
                Capability = need.Capability,
                Fingerprint = InstallCandidate.FingerprintOf("gcal", "Google Calendar", source),
            };
            return Task.FromResult<IReadOnlyList<CandidateReview>>([.. candidates.Select(_ => CandidateReview.Accept(accepted, []))]);
        }

        public Task<CandidateReview> ReviewUpdateAsync(IntegrationCandidate candidate, string appName, string integrationId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoInstaller : IIntegrationInstaller
    {
        public Task<InstallPlan> PlanAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => Task.FromResult(new InstallPlan());

        public Task<InstallOutcome> InstallAsync(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Nothing is installed by a handler: only a click does that.");

        public Task<InstallOutcome> UpdateAsync(InstallCandidate candidate, string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CleanUpAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private static IntegrationDiscoveryResult Found() =>
        new() { Status = DiscoveryStatus.Found, SourcesAnswered = ["mcp-registry"], Candidates = [Candidates.Make("example/google-calendar-mcp", description: "Calendar events for assistants")] };

    private static (ConnectedAppRequestHandler Handler, RecordingFinder Finder, CapabilityNeedSwitch Switch) Handler(
        ConnectedAppsFixture apps,
        IntegrationDiscoveryResult? discovery = null,
        bool switchOn = true,
        ICalendarProvider? calendar = null,
        IMessagingProvider? messaging = null,
        bool withSwitch = true)
    {
        var finder = new RecordingFinder(discovery ?? Found());
        var capabilitySwitch = new CapabilityNeedSwitch { IsOn = switchOn };
        var clock = new ManualTimeProvider();
        var handler = new ConnectedAppRequestHandler(
            ResolverFor(apps), finder, clock, NullLogger<ConnectedAppRequestHandler>.Instance, new AcceptingReviewer(),
            new IntegrationOffers(new NoInstaller(), clock, NullLogger<IntegrationOffers>.Instance), null, withSwitch ? capabilitySwitch : null, calendar, messaging);
        return (handler, finder, capabilitySwitch);
    }

    private static Task<ConnectedAppReply?> Ask(ConnectedAppRequestHandler handler, string? request = null) =>
        handler.TryAnswerAsync(new ToolContext(Guid.NewGuid(), request ?? WorkflowFixture.CanonicalRequest));

    [Fact]
    public async Task ByDefaultARequestThatNamesNoAppIsTheModelsAsItAlwaysWas()
    {
        await using var apps = Apps();
        var (handler, finder, _) = Handler(apps, switchOn: false);
        var (noSwitch, finder2, _) = Handler(apps, withSwitch: false);

        Assert.Null(await Ask(handler));
        Assert.Null(await Ask(noSwitch));
        Assert.Empty(finder.Asked);
        Assert.Empty(finder2.Asked);
    }

    [Fact]
    public async Task WhileItIsOnTheFirstNeedNothingServesGoesThroughTheFinderReviewAndOffer()
    {
        await using var apps = Apps();
        var (handler, finder, _) = Handler(apps);

        var reply = await Ask(handler);

        Assert.Equal(ConnectedAppReplyKind.InstallOffered, reply!.Kind);
        Assert.StartsWith("I can't read your events yet: no calendar integration is installed.", reply.Text, StringComparison.Ordinal);
        Assert.NotNull(reply.Offer);
        Assert.Equal("I didn't install a calendar integration, so I didn't read your events. Ask me again whenever you want to set it up.", reply.NotInstalledText);
        Assert.Equal(["calendar|read:event"], finder.Asked.Select(need => need.CacheKey));
        Assert.Equal("calendar", finder.Asked[0].AppName);
        Assert.DoesNotContain("exam", finder.Asked[0].AppName + finder.Asked[0].Capability.Phrase, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("brother", reply.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OnceTheCalendarIsInstalledTheNextNeedNothingServesIsTheMessagingApp()
    {
        await using var apps = Apps(WorkflowFixture.CalendarIntegration());
        var (handler, finder, _) = Handler(apps);

        var reply = await Ask(handler);

        Assert.StartsWith("I can't send a message yet: no messaging integration is installed.", reply!.Text, StringComparison.Ordinal);
        Assert.Equal("I didn't install a messaging integration, so I didn't send a message. Ask me again whenever you want to set it up.", reply.NotInstalledText);
        Assert.Equal(["messaging|send:message"], finder.Asked.Select(need => need.CacheKey));
    }

    [Fact]
    public async Task WhenEveryNeedIsServedTheRequestIsTheModelsAndNothingIsLookedFor()
    {
        await using var apps = Apps(WorkflowFixture.CalendarIntegration(), WorkflowFixture.MessagesIntegration());
        var (handler, finder, _) = Handler(apps);

        Assert.Null(await Ask(handler));
        Assert.Empty(finder.Asked);
    }

    [Fact]
    public async Task AProviderTheAppHasOfItsOwnServesAnEarlierNeedToo()
    {
        await using var apps = Apps();
        var (handler, finder, _) = Handler(apps, calendar: new InMemoryCalendarProvider(), messaging: new MockMessagingProvider());

        Assert.Null(await Ask(handler));
        Assert.Empty(finder.Asked);
    }

    [Fact]
    public async Task TheProviderThatOnlyReachesInstalledAppsDoesNotServeAnythingByItself()
    {
        await using var apps = Apps(WorkflowFixture.CalendarIntegration());
        await using var fixture = new WorkflowFixture(calendarApp: false, messagesApp: false);
        var (handler, finder, _) = Handler(apps, messaging: fixture.Provider);

        var reply = await Ask(handler);

        Assert.StartsWith("I can't send a message yet", reply!.Text, StringComparison.Ordinal);
        Assert.Equal(["messaging|send:message"], finder.Asked.Select(need => need.CacheKey));
    }

    [Fact]
    public async Task AProviderThatHasNoMessagingAppRightNowDoesNotServeTheNeed()
    {
        await using var apps = Apps(WorkflowFixture.CalendarIntegration());
        var unavailable = new UnavailableProvider();
        var (handler, _, _) = Handler(apps, messaging: unavailable);

        var reply = await Ask(handler);

        Assert.StartsWith("I can't send a message yet", reply!.Text, StringComparison.Ordinal);
    }

    private sealed class UnavailableProvider : IMessagingProvider
    {
        public string Name => "x";

        public bool IsSample => false;

        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<MessageDraft> CreateDraftAsync(Core.Messaging.OutgoingMessage message, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<MessageSendResult> SendAsync(MessageDraft draft, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public async Task AnInstalledIntegrationThatIsTurnedOffIsSaidSoAndNothingIsOffered()
    {
        await using var apps = Apps(WorkflowFixture.CalendarIntegration() with { Enabled = false });
        var (handler, finder, _) = Handler(apps);

        var reply = await Ask(handler);

        Assert.Equal(ConnectedAppReplyKind.InstalledNotUsable, reply!.Kind);
        Assert.StartsWith("I can't read your events right now: the calendar integration is installed but turned off", reply.Text, StringComparison.Ordinal);
        Assert.Empty(finder.Asked);
        Assert.Null(reply.Offer);
    }

    [Fact]
    public async Task WhenTheFinderIsNotAllowedToLookTheAnswerSaysSoInTheSameWords()
    {
        await using var apps = Apps();
        var (handler, _, _) = Handler(apps, new IntegrationDiscoveryResult { Status = DiscoveryStatus.Blocked, Block = DiscoveryBlock.LocalOnly });

        var reply = await Ask(handler);

        Assert.Equal(ConnectedAppReplyKind.DiscoveryBlocked, reply!.Kind);
        Assert.StartsWith("I can't read your events yet: no calendar integration is installed. To look for one I would have to search the web.", reply.Text, StringComparison.Ordinal);
        Assert.Contains("Only the app's name and what you want to do would be sent, never your text.", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARequestThatNamesAnAppIsAnsweredAsAlwaysWhileTheSwitchIsOn()
    {
        await using var apps = Apps();
        var (handler, finder, _) = Handler(apps);

        var reply = await Ask(handler, "Add 'buy milk' to Todoist");

        Assert.StartsWith("I can't create a task in Todoist yet", reply!.Text, StringComparison.Ordinal);
        Assert.Equal(["todoist|create:task"], finder.Asked.Select(need => need.CacheKey));
    }

    [Fact]
    public async Task ARequestThatIsNotAboutAnythingIsLeftAloneWhileTheSwitchIsOn()
    {
        await using var apps = Apps();
        var (handler, finder, _) = Handler(apps);

        Assert.Null(await Ask(handler, "What is 12 times 3?"));
        Assert.Empty(finder.Asked);
    }

    [Fact]
    public async Task TheSwitchIsReadForEveryRequestSoItCanBeTurnedOffAgain()
    {
        await using var apps = Apps();
        var (handler, finder, capabilitySwitch) = Handler(apps);

        Assert.NotNull(await Ask(handler));
        capabilitySwitch.IsOn = false;
        Assert.Null(await Ask(handler));
        Assert.Single(finder.Asked);
    }

    // ---- The words of the replies and of the review --------------------------------------------------------------------

    [Fact]
    public void TheWordsOfTheRepliesForAKindOfThingNeverSayInTheApp()
    {
        var calendar = IntegrationNeed.ForCalendar();

        Assert.Equal(
            "I didn't install a calendar integration, so I didn't read your events. Ask me again whenever you want to set it up.", IntegrationReplyWriter.NotInstalled(calendar, another: false));
        Assert.Equal(
            "I stopped looking for a calendar integration, so I didn't install one and I didn't read your events. Ask me again whenever you want me to look.",
            IntegrationReplyWriter.LookupStopped(calendar));
        Assert.Equal(
            "I didn't install the Todoist integration, so I didn't create a task in Todoist. Ask me again whenever you want to set it up.",
            IntegrationReplyWriter.NotInstalled(DiscoveryFixtures.Todoist, another: false));
    }

    [Theory]
    [InlineData("io.github.acme/google-calendar-mcp", "Google Calendar")]
    [InlineData("@scope/cal-server", "Cal")]
    [InlineData("mcp-messages", "Messages")]
    [InlineData("Sample_Calendar.mcp", "Sample Calendar")]
    [InlineData("a-b-c-d-e-f-g-h", "A B C D E")]
    [InlineData("x", "fallback")]
    [InlineData("", "fallback")]
    [InlineData(null, "fallback")]
    [InlineData("mcp", "Mcp")]
    public void AnIntegrationFoundForAKindOfThingIsKeptUnderItsOwnNameMadeReadable(string? candidateName, string expected)
    {
        Assert.Equal(expected, ReviewRules.AppNameFrom(candidateName, "fallback"));
    }

    [Fact]
    public async Task TheReviewOfWhatWasFoundForAKindOfThingIsInstalledUnderTheNameItHas()
    {
        var metadata = new FakePackageMetadata();
        metadata.Npm["@acme/google-calendar-mcp"] = Reviewable.NpmFactsFor("@acme/google-calendar-mcp");
        var registry = new InstalledIntegrationRegistry(new MemoryIntegrationStore(), NullLogger<InstalledIntegrationRegistry>.Instance);
        var reviewer = new CandidateReviewer(
            metadata, registry, TestSettings.LocalOnly(false), new FakePermissions(true), new ManualTimeProvider(Reviewable.Now), new CandidateReviewerOptions(),
            NullLogger<CandidateReviewer>.Instance);
        var candidate = Reviewable.NpmVendor(package: "@acme/google-calendar-mcp", tools: ["list_events", "create_event"], trust: CandidateTrust.Community) with
        {
            Name = "io.github.acme/google-calendar-mcp",
        };

        var review = await reviewer.ReviewAsync(candidate, IntegrationNeed.ForCalendar());

        Assert.True(review.IsAccepted, string.Join("; ", review.Blockers.Select(blocker => blocker.Text)));
        Assert.Equal("Google Calendar", review.Candidate!.AppName);
        Assert.Equal("googlecalendar", review.Candidate.Id);
        Assert.Equal("read event", review.Candidate.Capability!.Phrase);
        Assert.Contains("list_events", review.Candidate.MatchedTools);
    }

    // ---- The finder ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TheFinderSearchesForAKindOfThingWithTheWordForItAndNothingTheUserWrote()
    {
        var asked = new List<DiscoveryQuery>();
        var source = new FakeSource("mcp-registry", DiscoveryStage.First, (query, _) =>
        {
            asked.Add(query);
            return Task.FromResult<IReadOnlyList<IntegrationCandidate>>([Candidates.Make("acme/google-calendar-mcp", description: "Calendar events", evidence: CapabilityEvidence.Described)]);
        });
        var finder = new IntegrationFinder(
            [source], new FakeEnricher(), null, new MemoryDiscoveryCache(), TestSettings.LocalOnly(false), new FakePermissions(true),
            new ManualTimeProvider(WorkflowFixture.Start), new IntegrationFinderOptions(), NullLogger<IntegrationFinder>.Instance);

        var result = await finder.FindAsync(IntegrationNeed.ForCalendar());

        Assert.Equal(DiscoveryStatus.Found, result.Status);
        var query = Assert.Single(asked);
        Assert.Equal("calendar", query.Text);
        Assert.Equal("calendar", query.Term);
        Assert.Null(query.App);
        Assert.Empty(query.Owners);
        Assert.Equal("acme/google-calendar-mcp", Assert.Single(result.Candidates).Name);
    }

    [Fact]
    public async Task ACandidateNotAboutTheKindOfThingIsLeftOut()
    {
        var source = FakeSource.Returning("mcp-registry", DiscoveryStage.First, Candidates.Make("acme/todoist-mcp", description: "Tasks"));
        var finder = new IntegrationFinder(
            [source], new FakeEnricher(), null, new MemoryDiscoveryCache(), TestSettings.LocalOnly(false), new FakePermissions(true),
            new ManualTimeProvider(WorkflowFixture.Start), new IntegrationFinderOptions(), NullLogger<IntegrationFinder>.Instance);

        var result = await finder.FindAsync(IntegrationNeed.ForCalendar());

        Assert.Empty(result.Candidates);
    }
}
