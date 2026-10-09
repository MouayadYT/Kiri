using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>A place that knows of servers set up in other programs, as the test says.</summary>
internal sealed class FakeAvailableSource(Func<string, IReadOnlyList<AvailableIntegration>>? find = null, bool throws = false) : IAvailableIntegrationSource
{
    public List<string> Asked { get; } = [];

    public Task<IReadOnlyList<AvailableIntegration>> FindAsync(string appKey, CancellationToken cancellationToken = default)
    {
        Asked.Add(appKey);
        if (throws)
        {
            throw new InvalidOperationException("The source failed.");
        }

        return Task.FromResult(find?.Invoke(appKey) ?? []);
    }
}

/// <summary>Step 105: native and already-installed integrations are checked, in order, before anything is looked up.</summary>
public sealed class IntegrationResolverTests
{
    private const string AddMilk = "Add 'buy milk' to Todoist";

    private static IntegrationResolver ResolverFor(
        ConnectedAppsFixture apps, IReadOnlyList<IAvailableIntegrationSource>? sources = null, IntegrationResolverOptions? options = null) =>
        new(
            apps.Integrations,
            apps.Manager,
            apps.Settings,
            IntegrationRequestReader.Instance,
            sources ?? [],
            apps.Permissions,
            apps.Clock,
            options ?? new IntegrationResolverOptions(),
            NullLogger<IntegrationResolver>.Instance);

    private static InstalledIntegration Todoist(Func<InstalledIntegration, InstalledIntegration>? change = null) =>
        (change ?? (integration => integration))(Sample.Remote());

    [Fact]
    public async Task WithNothingInstalledAndNothingOnThePcTheFinderMayLook()
    {
        await using var apps = new ConnectedAppsFixture([]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync("Add 'buy milk' to Microsoft To Do");

        Assert.Equal(IntegrationResolutionKind.NotInstalled, resolution.Kind);
        Assert.True(resolution.DiscoveryAllowed);
        Assert.Equal("microsofttodo", resolution.Need!.AppKey);
        Assert.Equal("create task", resolution.Need.Capability.Phrase);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Theory]
    [InlineData("What is 2 plus 2")]
    [InlineData("hello")]
    [InlineData("How do I add a task in Todoist?")]
    [InlineData("")]
    public async Task ARequestThatIsNotForAnExternalAppIsLeftAlone(string request)
    {
        await using var apps = new ConnectedAppsFixture([Todoist()]);
        var source = new FakeAvailableSource();

        var resolution = await ResolverFor(apps, [source]).ResolveRequestAsync(request);

        Assert.Equal(IntegrationResolutionKind.NotAnAppRequest, resolution.Kind);
        Assert.False(resolution.DiscoveryAllowed);
        Assert.Equal(0, apps.Clients.CreateCalls);
        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task AnInstalledIntegrationThatOffersTheCapabilityIsReusedAtOnce()
    {
        await using var apps = new ConnectedAppsFixture([Todoist()]);
        var source = new FakeAvailableSource();

        var resolution = await ResolverFor(apps, [source]).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.UseInstalled, resolution.Kind);
        Assert.Equal("todoist", resolution.Integration!.Id);
        Assert.Equal(["createTask"], resolution.ToolNames);
        Assert.False(resolution.DiscoveryAllowed);
        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task TheToolNamesAreKeptSoTheNextRequestStartsAndReachesNothing()
    {
        var clock = new ManualTimeProvider();
        await using var apps = new ConnectedAppsFixture([Todoist()], clock: clock);
        var resolver = ResolverFor(apps);

        var first = await resolver.ResolveRequestAsync(AddMilk);
        Assert.False(first.FromCache);
        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Equal(["createTask", "listTasks"], (await apps.Integrations.GetAsync("todoist"))!.Capabilities.ToolNames);

        // Long after the connection manager's own list has gone stale, and the idle connection has been closed, the registry's names still answer.
        clock.Advance(TimeSpan.FromHours(2));
        var second = await resolver.ResolveRequestAsync("Please add a task to Todoist: call Anna");

        Assert.Equal(IntegrationResolutionKind.UseInstalled, second.Kind);
        Assert.True(second.FromCache);
        Assert.Equal(["createTask"], second.ToolNames);
        Assert.Equal(1, apps.Clients.CreateCalls);
        Assert.Equal(1, apps.Clients.Created[0].ListCalls);
    }

    [Fact]
    public async Task NamesThatAreOlderThanTheLifetimeAreReadAgain()
    {
        var clock = new ManualTimeProvider();
        await using var apps = new ConnectedAppsFixture([Todoist()], clock: clock);
        var resolver = ResolverFor(apps);
        await resolver.ResolveRequestAsync(AddMilk);

        clock.Advance(TimeSpan.FromHours(25));
        var again = await resolver.ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.UseInstalled, again.Kind);
        Assert.False(again.FromCache);

        // Moving the clock on also runs the idle check, which may end the first connection before the second request: what counts is that the tools were listed again.
        Assert.Equal(2, apps.Clients.Created.Sum(client => client.ListCalls));
    }

    [Fact]
    public async Task AnInstalledIntegrationIsFoundByAnyWayOfWritingTheAppsName()
    {
        await using var apps = new ConnectedAppsFixture([Sample.Remote("mstodo", "Microsoft To Do")]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync("Add 'buy milk' to MS To-Do");

        Assert.Equal(IntegrationResolutionKind.UseInstalled, resolution.Kind);
        Assert.Equal("mstodo", resolution.Integration!.Id);
    }

    [Fact]
    public async Task AnInstalledIntegrationWithNothingForTheCapabilityMayStillBeLookedFor()
    {
        await using var apps = new ConnectedAppsFixture([Todoist()]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync("Show my events in Todoist");

        // Todoist offers tasks, not events.
        Assert.Equal(IntegrationResolutionKind.InstalledNotUsable, resolution.Kind);
        Assert.Equal(InstalledProblem.LacksCapability, resolution.Problem);
        Assert.True(resolution.DiscoveryAllowed);
    }

    [Fact]
    public async Task AnInstalledIntegrationThatOnlyNeedsFixingIsNeverLookedForAgain()
    {
        await using var apps = new ConnectedAppsFixture([Todoist(app => app with { Enabled = false })]);
        var source = new FakeAvailableSource(_ => [new AvailableIntegration("todoist", "Claude Desktop", McpTransportKind.Stdio)]);

        var resolution = await ResolverFor(apps, [source]).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.InstalledNotUsable, resolution.Kind);
        Assert.Equal(InstalledProblem.Disabled, resolution.Problem);
        Assert.False(resolution.DiscoveryAllowed);
        Assert.Empty(source.Asked);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnIntegrationThatWouldSendThingsOffThisPcIsNotUsableWhileLocalOnlyIsOn()
    {
        await using var apps = new ConnectedAppsFixture([Todoist()], localOnly: true);

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(InstalledProblem.BlockedByLocalOnly, resolution.Problem);
        Assert.False(resolution.DiscoveryAllowed);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnIntegrationOnThisPcWorksUnderLocalOnly()
    {
        await using var apps = new ConnectedAppsFixture([Sample.Loopback("todoist", "Todoist")], localOnly: true);

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.UseInstalled, resolution.Kind);
    }

    [Fact]
    public async Task APermissionTheIntegrationNeedsMustBeOn()
    {
        var app = Todoist(integration => integration with { Permissions = new IntegrationPermissions { RequiredCapability = PermissionCapability.Files } });
        await using var apps = new ConnectedAppsFixture([app], permissions: new DenyingPermissions(PermissionCapability.Files));

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(InstalledProblem.PermissionOff, resolution.Problem);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task APermissionSetToAskEachTimeIsNotAnObstacleBecauseTheUserIsAskedWhenAToolIsUsed()
    {
        var app = Todoist(integration => integration with { Permissions = new IntegrationPermissions { RequiredCapability = PermissionCapability.Calendar } });
        var asking = Assistant.Core.Permissions.SettingsPermissionPolicy.Decide(
            Assistant.Core.Permissions.PermissionSettingsExtensions.WithMode(new Assistant.Core.Settings.PermissionSettings(), PermissionCapability.Calendar, PermissionMode.AskEveryTime), PermissionCapability.Calendar);
        await using var apps = new ConnectedAppsFixture([app], permissions: new FixedDecision(asking));

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.UseInstalled, resolution.Kind);
    }

    [Fact]
    public async Task WhatTheUserTurnedOffForTheAppIsSaidAndTheAppIsNotConnectedToOrLookedForAgain()
    {
        var source = new FakeAvailableSource(_ => []);
        foreach (var (change, problem) in new (Func<IntegrationPermissions, IntegrationPermissions>, InstalledProblem)[]
                 {
                     (permissions => permissions with { AllowNetwork = false }, InstalledProblem.NetworkOff),
                     (permissions => permissions with { AllowReads = false, AllowSideEffects = false }, InstalledProblem.AccessOff),
                 })
        {
            await using var apps = new ConnectedAppsFixture([Todoist(app => app with { Permissions = change(app.Permissions) })]);

            var resolution = await ResolverFor(apps, [source]).ResolveRequestAsync(AddMilk);

            Assert.Equal(IntegrationResolutionKind.InstalledNotUsable, resolution.Kind);
            Assert.Equal(problem, resolution.Problem);
            Assert.False(resolution.DiscoveryAllowed);
            Assert.Equal(0, apps.Clients.CreateCalls);
        }

        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task AnAppThatSignsInWhoseAccountWasTakenAwayIsNotUsable()
    {
        var app = Todoist(integration => integration with
        {
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.BearerToken, State = IntegrationAuthState.Ready, Secrets = [new IntegrationSecretBinding("Authorization", "todoist.token")] },
            Permissions = integration.Permissions with { AllowAccountAccess = false },
        });
        await using var apps = new ConnectedAppsFixture([app]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(InstalledProblem.AccountAccessOff, resolution.Problem);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    private sealed class FixedDecision(PermissionDecision decision) : Assistant.Core.Contracts.IPermissionPolicy
    {
        public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default) =>
            Task.FromResult(decision with { Capability = capability });
    }

    [Theory]
    [InlineData(IntegrationAuthState.NeedsSignIn)]
    [InlineData(IntegrationAuthState.Expired)]
    [InlineData(IntegrationAuthState.Rejected)]
    public async Task AnIntegrationThatNeedsASignInSaysSoWithoutBeingConnectedTo(IntegrationAuthState state)
    {
        await using var apps = new ConnectedAppsFixture(
            [Todoist(app => app with { Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.OAuth, State = state } })]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(InstalledProblem.NeedsSignIn, resolution.Problem);
        Assert.False(resolution.DiscoveryAllowed);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnIntegrationMarkedIncompatibleIsNotConnectedToAgain()
    {
        await using var apps = new ConnectedAppsFixture(
            [Todoist(app => app with { Health = new IntegrationHealth { Status = IntegrationHealthStatus.Incompatible } })]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(InstalledProblem.Incompatible, resolution.Problem);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task AnIntegrationThatCannotBeReachedIsReportedAsSuchAndNotLookedForAgain()
    {
        await using var apps = new ConnectedAppsFixture(
            [Todoist()], clients: _ => new StubMcpClient { ConnectFailure = new McpException(McpFailure.ConnectFailed) });

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.InstalledNotUsable, resolution.Kind);
        Assert.Equal(InstalledProblem.Unreachable, resolution.Problem);
        Assert.False(resolution.DiscoveryAllowed);
    }

    [Fact]
    public async Task AServerThatAsksForASignInWhenConnectedToIsReportedAsNeedingOne()
    {
        await using var apps = new ConnectedAppsFixture(
            [Todoist()], clients: _ => new StubMcpClient { ConnectFailure = new McpException(McpFailure.AuthRequired) });

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(InstalledProblem.NeedsSignIn, resolution.Problem);
    }

    [Fact]
    public async Task ARequestToDeleteIsRefusedWithoutLookingAtAnything()
    {
        await using var apps = new ConnectedAppsFixture([Todoist()]);
        var source = new FakeAvailableSource();

        var resolution = await ResolverFor(apps, [source]).ResolveRequestAsync("Delete the task 'buy milk' in Todoist");

        Assert.Equal(IntegrationResolutionKind.Refused, resolution.Kind);
        Assert.False(resolution.DiscoveryAllowed);
        Assert.Equal(0, apps.Clients.CreateCalls);
        Assert.Empty(source.Asked);
    }

    [Fact]
    public async Task AServerAlreadySetUpInAnotherProgramIsFoundBeforeAnythingIsLookedUp()
    {
        await using var apps = new ConnectedAppsFixture([]);
        var source = new FakeAvailableSource(_ => [new AvailableIntegration("microsoft-todo", "Claude Desktop", McpTransportKind.Stdio)]);

        var resolution = await ResolverFor(apps, [source]).ResolveRequestAsync("Add 'buy milk' to Microsoft To Do");

        Assert.Equal(IntegrationResolutionKind.AvailableLocally, resolution.Kind);
        Assert.Equal("microsofttodo", Assert.Single(source.Asked));
        Assert.Equal("Claude Desktop", Assert.Single(resolution.Available).Where);
        Assert.False(resolution.DiscoveryAllowed);
    }

    [Fact]
    public async Task ASourceThatFailsIsOneThatFoundNothing()
    {
        await using var apps = new ConnectedAppsFixture([]);

        var resolution = await ResolverFor(apps, [new FakeAvailableSource(throws: true), new FakeAvailableSource()]).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.NotInstalled, resolution.Kind);
    }

    [Fact]
    public async Task OnlyTheAppTheRequestIsAboutIsAskedAbout()
    {
        await using var apps = new ConnectedAppsFixture([Sample.Remote("notion", "Notion")]);
        var source = new FakeAvailableSource();

        var resolution = await ResolverFor(apps, [source]).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.NotInstalled, resolution.Kind);
        Assert.Equal(["todoist"], source.Asked);
        Assert.Equal(0, apps.Clients.CreateCalls);
    }

    [Fact]
    public async Task WhenOneOfTwoIntegrationsForTheAppWorksItIsUsed()
    {
        var off = Sample.Remote("todoistold", "Todoist") with { Enabled = false };
        await using var apps = new ConnectedAppsFixture([off, Sample.Remote("todoist", "Todoist")]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.UseInstalled, resolution.Kind);
        Assert.Equal("todoist", resolution.Integration!.Id);
    }

    [Fact]
    public async Task WhatCanBePutRightIsReportedBeforeWhatLacksTheCapability()
    {
        var off = Sample.Remote("todoistold", "Todoist") with { Enabled = false };
        await using var apps = new ConnectedAppsFixture([Sample.Remote("todoist", "Todoist"), off]);

        var resolution = await ResolverFor(apps).ResolveRequestAsync("Show my events in Todoist");

        // The working one has no events; the disabled one is the one that can be put right, so it is what the user is told.
        Assert.Equal(InstalledProblem.Disabled, resolution.Problem);
        Assert.False(resolution.DiscoveryAllowed);
    }

    [Fact]
    public async Task InstalledIntegrationsThatCannotBeReadMeanNothingCanBeSaid()
    {
        await using var apps = new ConnectedAppsFixture([], store: new MemoryIntegrationStore { FailReads = true });

        var resolution = await ResolverFor(apps).ResolveRequestAsync(AddMilk);

        Assert.Equal(IntegrationResolutionKind.CouldNotCheck, resolution.Kind);
        Assert.False(resolution.DiscoveryAllowed);
    }

    [Fact]
    public async Task TheResolverNeverNeedsAModelOrTheNetwork()
    {
        // Everything it uses is a registry, a connection manager, the settings, a reader and sources on this PC: nothing here can browse.
        var constructor = typeof(IntegrationResolver).GetConstructors().Single();
        var parameters = constructor.GetParameters().Select(parameter => parameter.ParameterType.Name).ToList();
        Assert.DoesNotContain(parameters, name => name.Contains("Model", StringComparison.Ordinal) || name.Contains("Finder", StringComparison.Ordinal) || name.Contains("Http", StringComparison.Ordinal));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ALoggerThatSeesEverythingNeverSeesTheRequestOrTheAppsName()
    {
        var logger = new CapturingLoggerFactory();
        await using var apps = new ConnectedAppsFixture([]);
        var resolver = new IntegrationResolver(
            apps.Integrations, apps.Manager, apps.Settings, IntegrationRequestReader.Instance, [], apps.Permissions, apps.Clock, new IntegrationResolverOptions(),
            logger.CreateLogger<IntegrationResolver>());

        await resolver.ResolveRequestAsync("Add 'my secret milk' to Microsoft To Do");

        Assert.NotEmpty(logger.Lines);
        Assert.DoesNotContain(logger.Lines, line => line.Contains("secret", StringComparison.OrdinalIgnoreCase) || line.Contains("Microsoft", StringComparison.OrdinalIgnoreCase));
    }
}
