using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Permissions;
using Assistant.Core.Settings;
using Assistant.Tools.Integrations;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>Steps 105-106 together: a request for an external app is resolved in order, and only then is anything looked up.</summary>
public sealed class ConnectedAppRequestHandlerTests
{
    private const string AddMilk = "Add 'buy my secret milk' to Microsoft To Do";

    private sealed class FakeResolver(IntegrationResolution resolution) : IIntegrationResolver
    {
        public Task<IntegrationResolution> ResolveRequestAsync(string? request, CancellationToken cancellationToken = default) => Task.FromResult(resolution);

        public Task<IntegrationResolution> ResolveAsync(IntegrationNeed need, CancellationToken cancellationToken = default) => Task.FromResult(resolution);
    }

    private sealed class FakeFinder(IntegrationDiscoveryResult? result = null, bool throws = false) : IIntegrationFinder
    {
        public List<(IntegrationNeed Need, IReadOnlyCollection<string>? Exclude)> Asked { get; } = [];

        public Task<IntegrationDiscoveryResult> FindAsync(IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default)
        {
            Asked.Add((need, exclude));
            return throws
                ? throw new InvalidOperationException("The finder failed.")
                : Task.FromResult(result ?? new IntegrationDiscoveryResult { Status = DiscoveryStatus.NothingPlausible, SourcesAnswered = ["github"] });
        }
    }

    private static ConnectedAppRequestHandler Handler(IIntegrationResolver resolver, IIntegrationFinder finder) =>
        new(resolver, finder, new ManualTimeProvider(), NullLogger<ConnectedAppRequestHandler>.Instance);

    private static ToolContext Context(string? request) => new(Guid.NewGuid(), request);

    // ---- with the parts replaced ----

    [Theory]
    [InlineData(IntegrationResolutionKind.NotAnAppRequest)]
    [InlineData(IntegrationResolutionKind.CouldNotCheck)]
    public async Task ARequestThatIsNotForAnExternalAppIsLeftToTheModel(IntegrationResolutionKind kind)
    {
        var finder = new FakeFinder();
        var resolution = kind == IntegrationResolutionKind.NotAnAppRequest ? IntegrationResolution.NotAnAppRequest : IntegrationResolution.CouldNotCheck;

        Assert.Null(await Handler(new FakeResolver(resolution), finder).TryAnswerAsync(Context("hello")));
        Assert.Empty(finder.Asked);
    }

    [Fact]
    public async Task AWorkingInstalledIntegrationIsLeftToTheModelAndNothingIsLookedUp()
    {
        var finder = new FakeFinder();
        var resolution = IntegrationResolution.Use(DiscoveryFixtures.Todoist, Sample.Remote(), ["createTask"], fromCache: true);

        Assert.Null(await Handler(new FakeResolver(resolution), finder).TryAnswerAsync(Context("Add milk to Todoist")));
        Assert.Empty(finder.Asked);
    }

    [Fact]
    public async Task OnlyAMissingIntegrationSendsTheFinderToLook()
    {
        var finder = new FakeFinder();

        var reply = await Handler(new FakeResolver(IntegrationResolution.Missing(DiscoveryFixtures.MicrosoftTodo)), finder).TryAnswerAsync(Context(AddMilk));

        Assert.Equal(DiscoveryFixtures.MicrosoftTodo, Assert.Single(finder.Asked).Need);
        Assert.Equal(ConnectedAppReplyKind.DiscoveryEmpty, reply!.Kind);
    }

    [Theory]
    [InlineData(InstalledProblem.Disabled)]
    [InlineData(InstalledProblem.NeedsSignIn)]
    [InlineData(InstalledProblem.Unreachable)]
    [InlineData(InstalledProblem.Incompatible)]
    [InlineData(InstalledProblem.BlockedByLocalOnly)]
    [InlineData(InstalledProblem.PermissionOff)]
    public async Task AnInstalledIntegrationThatNeedsFixingIsNeverLookedForAgain(InstalledProblem problem)
    {
        var finder = new FakeFinder();
        var resolution = IntegrationResolution.NotUsable(DiscoveryFixtures.Todoist, Sample.Remote(), problem);

        var reply = await Handler(new FakeResolver(resolution), finder).TryAnswerAsync(Context("Add milk to Todoist"));

        Assert.Equal(ConnectedAppReplyKind.InstalledNotUsable, reply!.Kind);
        Assert.Empty(finder.Asked);
    }

    [Fact]
    public async Task AnInstalledIntegrationWithNothingForTheThingSendsTheFinderButWithoutOfferingTheSameAgain()
    {
        var finder = new FakeFinder();
        var installed = Sample.Remote() with { Source = new IntegrationSource(IntegrationSourceKind.OfficialRegistry, "io.github.Doist/todoist-mcp") };
        var resolution = IntegrationResolution.NotUsable(DiscoveryFixtures.Todoist, installed, InstalledProblem.LacksCapability);

        var reply = await Handler(new FakeResolver(resolution), finder).TryAnswerAsync(Context("Add milk to Todoist"));

        Assert.Equal(["io.github.Doist/todoist-mcp"], Assert.Single(finder.Asked).Exclude);
        Assert.StartsWith("The Todoist integration you have installed doesn't offer a way to create a task.", reply!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARefusedRequestAndAServerAlreadyOnThisPcAreAnsweredWithoutTheFinder()
    {
        var finder = new FakeFinder();

        var refused = await Handler(new FakeResolver(IntegrationResolution.Refuse(DiscoveryFixtures.Todoist)), finder).TryAnswerAsync(Context("Delete it from Todoist"));
        var local = await Handler(
            new FakeResolver(IntegrationResolution.Local(DiscoveryFixtures.Todoist, [new AvailableIntegration("todoist", "VS Code", Assistant.Tools.Mcp.McpTransportKind.StreamableHttp)])), finder)
            .TryAnswerAsync(Context("Add milk to Todoist"));

        Assert.Equal(ConnectedAppReplyKind.Refused, refused!.Kind);
        Assert.Equal(ConnectedAppReplyKind.FoundOnThisPc, local!.Kind);
        Assert.Empty(finder.Asked);
    }

    [Fact]
    public async Task AFinderThatFailsLeavesTheRequestToTheModel()
    {
        var reply = await Handler(new FakeResolver(IntegrationResolution.Missing(DiscoveryFixtures.Todoist)), new FakeFinder(throws: true)).TryAnswerAsync(Context("Add milk to Todoist"));

        Assert.Null(reply);
    }

    [Fact]
    public async Task StoppingIsNotSwallowed()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var handler = Handler(new CancellingResolver(), new FakeFinder());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.TryAnswerAsync(Context("Add milk to Todoist"), source.Token));
    }

    private sealed class CancellingResolver : IIntegrationResolver
    {
        public Task<IntegrationResolution> ResolveRequestAsync(string? request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(IntegrationResolution.NotAnAppRequest);
        }

        public Task<IntegrationResolution> ResolveAsync(IntegrationNeed need, CancellationToken cancellationToken = default) => ResolveRequestAsync(null, cancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithNoRequestThereIsNothingToHandle(string? request)
    {
        var finder = new FakeFinder();
        Assert.Null(await Handler(new FakeResolver(IntegrationResolution.Missing(DiscoveryFixtures.Todoist)), finder).TryAnswerAsync(Context(request)));
        Assert.Empty(finder.Asked);
    }

    // ---- the whole path, with the real resolver and finder ----

    private sealed class Whole : IAsyncDisposable
    {
        public Whole(IEnumerable<InstalledIntegration> installed, bool localOnly, bool webAllowed, params IIntegrationDiscoverySource[] sources)
        {
            Apps = new ConnectedAppsFixture(installed, localOnly: localOnly);
            Sources = sources;
            Cache = new MemoryDiscoveryCache();
            var resolver = new IntegrationResolver(
                Apps.Integrations, Apps.Manager, Apps.Settings, IntegrationRequestReader.Instance, [], Apps.Permissions, Apps.Clock, new IntegrationResolverOptions(),
                NullLogger<IntegrationResolver>.Instance);
            var finder = new IntegrationFinder(
                sources, new FakeEnricher(), null, Cache, Apps.Settings, new FakePermissions(webAllowed), Apps.Clock, new IntegrationFinderOptions(),
                NullLogger<IntegrationFinder>.Instance);
            Handler = new ConnectedAppRequestHandler(resolver, finder, Apps.Clock, NullLogger<ConnectedAppRequestHandler>.Instance);
        }

        public ConnectedAppsFixture Apps { get; }

        public IIntegrationDiscoverySource[] Sources { get; }

        public MemoryDiscoveryCache Cache { get; }

        public ConnectedAppRequestHandler Handler { get; }

        public ValueTask DisposeAsync() => Apps.DisposeAsync();
    }

    private static FakeSource MicrosoftTodoSource() => FakeSource.Returning(
        "github",
        DiscoveryStage.First,
        Candidates.Make("MAG-Cie/mcp-microsoft-todo", description: "MCP server for Microsoft To Do via Microsoft Graph API", activity: new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
        Candidates.Make("fan/notion-mcp", description: "Notion"));

    [Fact]
    public async Task TheExampleOfStep106WithTheDefaultSettingsEntersDiscoveryButDoesNotLook()
    {
        // Local Only is on by default, so the Assistant says what it would need and sends nothing.
        var source = MicrosoftTodoSource();
        await using var whole = new Whole([], localOnly: true, webAllowed: false, source);

        var reply = await whole.Handler.TryAnswerAsync(Context(AddMilk));

        Assert.Equal(ConnectedAppReplyKind.DiscoveryBlocked, reply!.Kind);
        Assert.Contains("no Microsoft To Do integration is installed", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, source.Asked);
    }

    [Fact]
    public async Task WithTheLocksOpenTheFinderLooksAndTheAnswerListsWhatItFoundWithoutTheUsersWords()
    {
        var source = MicrosoftTodoSource();
        await using var whole = new Whole([], localOnly: false, webAllowed: true, source);

        var reply = await whole.Handler.TryAnswerAsync(Context(AddMilk));

        Assert.Equal(ConnectedAppReplyKind.DiscoveryFound, reply!.Kind);
        Assert.Contains("`MAG-Cie/mcp-microsoft-todo`", reply.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("notion", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", reply.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, source.Asked);
        Assert.Equal(0, whole.Apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AskingTheSameThingAgainIsAnsweredFromWhatWasKept()
    {
        var source = MicrosoftTodoSource();
        await using var whole = new Whole([], localOnly: false, webAllowed: true, source);

        var first = await whole.Handler.TryAnswerAsync(Context(AddMilk));
        var second = await whole.Handler.TryAnswerAsync(Context("Add 'call Anna' to Microsoft To Do"));

        Assert.Equal(1, source.Asked);
        Assert.NotEqual(first!.Text, second!.Text);
        Assert.Contains("in a search I made earlier", second.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAlreadyInstalledWorkingIntegrationIsUsedAndNothingIsEverLookedUp()
    {
        var source = MicrosoftTodoSource();
        await using var whole = new Whole([Sample.Remote("todoist", "Todoist")], localOnly: false, webAllowed: true, source);

        var reply = await whole.Handler.TryAnswerAsync(Context("Add 'buy milk' to Todoist"));

        Assert.Null(reply);
        Assert.Equal(0, source.Asked);
    }

    [Fact]
    public async Task AnInstalledIntegrationThatIsOffIsReportedAndNothingIsLookedUp()
    {
        var source = MicrosoftTodoSource();
        await using var whole = new Whole([Sample.Remote("todoist", "Todoist") with { Enabled = false }], localOnly: false, webAllowed: true, source);

        var reply = await whole.Handler.TryAnswerAsync(Context("Add 'buy milk' to Todoist"));

        Assert.Equal(ConnectedAppReplyKind.InstalledNotUsable, reply!.Kind);
        Assert.Contains("turned off", reply.Text, StringComparison.Ordinal);
        Assert.Equal(0, source.Asked);
    }

    [Fact]
    public async Task ARequestToDeleteIsRefusedWhateverIsInstalled()
    {
        await using var whole = new Whole([Sample.Remote("todoist", "Todoist")], localOnly: false, webAllowed: true, MicrosoftTodoSource());

        var reply = await whole.Handler.TryAnswerAsync(Context("Delete the milk task in Todoist"));

        Assert.Equal(ConnectedAppReplyKind.Refused, reply!.Kind);
        Assert.Equal(0, whole.Apps.Clients.CreateCalls);
    }

    [Theory]
    [InlineData("What is 2 plus 2")]
    [InlineData("How do I add a task in Microsoft To Do?")]
    [InlineData("Open Spotify")]
    [InlineData("hello")]
    public async Task OrdinaryRequestsAreNeverInterceptedAndCostNothing(string request)
    {
        var source = MicrosoftTodoSource();
        await using var whole = new Whole([], localOnly: false, webAllowed: true, source);

        Assert.Null(await whole.Handler.TryAnswerAsync(Context(request)));
        Assert.Equal(0, source.Asked);
        Assert.Equal(0, whole.Cache.Reads);
    }

    // ---- a web look-up when External Web and Image Search is set to ask every time (step 119) ----

    private sealed class Prompt(ConfirmationDecision answer) : IPermissionPrompt
    {
        public List<PermissionPromptRequest> Asked { get; } = [];

        public Task<ConfirmationDecision> AskAsync(PermissionPromptRequest request, CancellationToken cancellationToken = default)
        {
            Asked.Add(request);
            return Task.FromResult(answer);
        }
    }

    private static async Task<(ConnectedAppReply? Reply, FakeSource Source, Prompt Prompt)> AskedAboutAsync(
        PermissionMode mode, ConfirmationDecision answer, bool localOnly = false, bool withGate = true)
    {
        var source = MicrosoftTodoSource();
        var settings = new FixedSettings
        {
            Current = new AppSettings
            {
                Privacy = new PrivacySettings { LocalOnly = localOnly },
                Permissions = new PermissionSettings().WithMode(PermissionCapability.ExternalSearch, mode),
            },
        };
        var policy = new SettingsPermissionPolicy(settings);
        var prompt = new Prompt(answer);
        await using var apps = new ConnectedAppsFixture([], localOnly: localOnly);
        var resolver = new IntegrationResolver(
            apps.Integrations, apps.Manager, settings, IntegrationRequestReader.Instance, [], policy, apps.Clock, new IntegrationResolverOptions(), NullLogger<IntegrationResolver>.Instance);
        var finder = new IntegrationFinder(
            [source], new FakeEnricher(), null, new MemoryDiscoveryCache(), settings, policy, apps.Clock, new IntegrationFinderOptions(), NullLogger<IntegrationFinder>.Instance);
        var gate = new PermissionGate(policy, prompt, NullLogger<PermissionGate>.Instance);
        var handler = new ConnectedAppRequestHandler(
            resolver, finder, apps.Clock, NullLogger<ConnectedAppRequestHandler>.Instance, gate: withGate ? gate : null, settings: settings);

        return (await handler.TryAnswerAsync(Context(AddMilk)), source, prompt);
    }

    [Fact]
    public async Task ALookUpIsAskedAboutAndOnlyAYesLetsTheFinderLookAndTheQuestionNamesTheAppAndNotWhatWasWanted()
    {
        var (reply, source, prompt) = await AskedAboutAsync(PermissionMode.AskEveryTime, ConfirmationDecision.Approved);

        Assert.NotEqual(ConnectedAppReplyKind.DiscoveryBlocked, reply!.Kind);
        Assert.True(source.Asked > 0);
        var asked = Assert.Single(prompt.Asked);
        Assert.Equal(PermissionCapability.ExternalSearch, asked.Capability);
        Assert.Contains("Microsoft To Do", asked.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", asked.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(ConfirmationDecision.Declined)]
    [InlineData(ConfirmationDecision.NoAnswer)]
    [InlineData(ConfirmationDecision.CouldNotAsk)]
    public async Task WithoutAYesNothingIsSentAndTheAnswerSaysTheUserDidNotAllowIt(ConfirmationDecision answer)
    {
        var (reply, source, prompt) = await AskedAboutAsync(PermissionMode.AskEveryTime, answer);

        Assert.Equal(ConnectedAppReplyKind.DiscoveryBlocked, reply!.Kind);
        Assert.Contains("did not allow", reply.Text, StringComparison.Ordinal);
        Assert.Equal(0, source.Asked);
        Assert.Single(prompt.Asked);
    }

    [Fact]
    public async Task WithNoOneToAskAPermissionSetToAskIsNeverLookedUpWith()
    {
        var (reply, source, _) = await AskedAboutAsync(PermissionMode.AskEveryTime, ConfirmationDecision.Approved, withGate: false);

        Assert.Equal(ConnectedAppReplyKind.DiscoveryBlocked, reply!.Kind);
        Assert.Equal(0, source.Asked);
    }

    [Fact]
    public async Task AllowedAsksNothingAndLocalOnlyIsRefusedWithoutAQuestion()
    {
        var (allowed, allowedSource, allowedPrompt) = await AskedAboutAsync(PermissionMode.Allowed, ConfirmationDecision.Declined);
        var (localOnly, localSource, localPrompt) = await AskedAboutAsync(PermissionMode.AskEveryTime, ConfirmationDecision.Approved, localOnly: true);

        Assert.NotEqual(ConnectedAppReplyKind.DiscoveryBlocked, allowed!.Kind);
        Assert.True(allowedSource.Asked > 0);
        Assert.Empty(allowedPrompt.Asked);
        Assert.Equal(ConnectedAppReplyKind.DiscoveryBlocked, localOnly!.Kind);
        Assert.Contains("Local Only", localOnly.Text, StringComparison.Ordinal);
        Assert.Equal(0, localSource.Asked);
        Assert.Empty(localPrompt.Asked);
    }

    [Fact]
    public async Task ALoggerThatSeesEverythingNeverSeesTheRequestTheAppOrWhatWasFound()
    {
        var logger = new CapturingLoggerFactory();
        await using var apps = new ConnectedAppsFixture([]);
        var resolver = new IntegrationResolver(
            apps.Integrations, apps.Manager, apps.Settings, IntegrationRequestReader.Instance, [], apps.Permissions, apps.Clock, new IntegrationResolverOptions(), logger.CreateLogger<IntegrationResolver>());
        var finder = new IntegrationFinder(
            [MicrosoftTodoSource()], new FakeEnricher(), null, new MemoryDiscoveryCache(), TestSettings.LocalOnly(false), new FakePermissions(true), apps.Clock,
            new IntegrationFinderOptions(), logger.CreateLogger<IntegrationFinder>());
        var handler = new ConnectedAppRequestHandler(resolver, finder, apps.Clock, logger.CreateLogger<ConnectedAppRequestHandler>());

        await handler.TryAnswerAsync(Context(AddMilk));

        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, line =>
            line.Contains("secret", StringComparison.OrdinalIgnoreCase) || line.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) || line.Contains("MAG-Cie", StringComparison.Ordinal));
    }
}
