using System.Net;
using System.Net.Sockets;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Integrations;

/// <summary>
/// Connecting an app whose server the Assistant knows (PROJECT_SPEC §4.8): the request is answered with an offer to connect and sign in, nothing is searched for, and what the user
/// approves is recorded, signed in to in the browser and checked. The authorization server is a fake; the sign-in's port on this PC is real.
/// </summary>
public sealed class ConnectKnownAppsTests
{
    private static readonly KnownEndpoint Todoist = new("todoist", "Todoist", "https://mcp.example.com/mcp", "todoist.com");

    private static readonly IntegrationNeed CreateTask = new("Todoist", "todoist", new IntegrationCapability(CapabilityAction.Create, "task"), IntegrationNeedSource.Catalog);

    private sealed class Resolver(IntegrationResolution resolution) : IIntegrationResolver
    {
        public Task<IntegrationResolution> ResolveRequestAsync(string? request, CancellationToken cancellationToken = default) => Task.FromResult(resolution);

        public Task<IntegrationResolution> ResolveAsync(IntegrationNeed need, CancellationToken cancellationToken = default) => Task.FromResult(resolution);
    }

    private sealed class NeverFinder : IIntegrationFinder
    {
        public int Asked { get; private set; }

        public Task<IntegrationDiscoveryResult> FindAsync(IntegrationNeed need, IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default)
        {
            Asked++;
            return Task.FromResult(new IntegrationDiscoveryResult { Status = DiscoveryStatus.NothingPlausible });
        }
    }

    private sealed class RecordingConnector : IIntegrationConnector
    {
        public List<string> Calls { get; } = [];

        public Task<InstallOutcome> ConnectAsync(
            KnownEndpoint endpoint, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default,
            Assistant.Tools.Mcp.Auth.OAuthSignInOptions? options = null)
        {
            Calls.Add("connect:" + endpoint.AppKey);
            return Task.FromResult(new InstallOutcome { Status = InstallStatus.Installed, Message = "Connected." });
        }

        public Task<InstallOutcome> SignInAsync(string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default, Assistant.Tools.Mcp.Auth.OAuthSignInOptions? options = null)
        {
            Calls.Add("signin:" + integrationId);
            return Task.FromResult(new InstallOutcome { Status = InstallStatus.Installed, Message = "Signed in." });
        }

        public Task SignOutAsync(string integrationId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<InstallOutcome> UseTokenAsync(string integrationId, string token, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallOutcome { Status = InstallStatus.Installed, Message = "Done." });

        public Task<InstallOutcome> SetKeyAsync(string integrationId, string name, string value, CancellationToken cancellationToken = default) =>
            Task.FromResult(new InstallOutcome { Status = InstallStatus.Installed, Message = "Done." });
    }

    private sealed class NoInstaller : IIntegrationInstaller
    {
        public Task<InstallPlan> PlanAsync(InstallCandidate candidate, CancellationToken cancellationToken = default) => Task.FromResult(new InstallPlan());

        public Task<InstallOutcome> InstallAsync(InstallCandidate candidate, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<InstallOutcome> UpdateAsync(InstallCandidate candidate, string integrationId, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> CleanUpAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private static (ConnectedAppRequestHandler Handler, NeverFinder Finder, RecordingConnector Connector) Handler(
        IntegrationResolution resolution, bool localOnly = false, bool known = true)
    {
        var finder = new NeverFinder();
        var connector = new RecordingConnector();
        var clock = new ManualTimeProvider();
        var offers = new IntegrationOffers(new NoInstaller(), clock, NullLogger<IntegrationOffers>.Instance, connector: connector);
        var handler = new ConnectedAppRequestHandler(
            new Resolver(resolution), finder, clock, NullLogger<ConnectedAppRequestHandler>.Instance, offers: offers, settings: TestSettings.LocalOnly(localOnly),
            knownEndpoints: known ? KnownEndpoints.For : null);
        return (handler, finder, connector);
    }

    private static Task<ConnectedAppReply?> Ask(ConnectedAppRequestHandler handler, string request = "Add milk to Todoist") =>
        handler.TryAnswerAsync(new ToolContext(Guid.NewGuid(), request));

    // ---- The answer ----

    [Fact]
    public async Task AKnownAppThatIsNotConnectedIsOfferedAConnectionAndNothingIsSearchedFor()
    {
        var (handler, finder, connector) = Handler(IntegrationResolution.Missing(CreateTask));

        var reply = await Ask(handler);

        Assert.Equal(ConnectedAppReplyKind.ConnectOffered, reply!.Kind);
        Assert.Equal(IntegrationOfferKind.Connect, reply.Offer!.Kind);
        Assert.Equal("Todoist", reply.Offer.AppName);
        Assert.Equal(IntegrationOfferMaker.Official, reply.Offer.Maker);
        Assert.Contains("never see your password", reply.Text, StringComparison.Ordinal);
        Assert.Contains("didn't connect Todoist", reply.NotInstalledText, StringComparison.Ordinal);
        Assert.Equal(0, finder.Asked);
        Assert.Empty(connector.Calls);
    }

    [Fact]
    public async Task AnAppOnThisPcIsOfferedEvenWhileLocalOnlyModeIsOn()
    {
        var beeper = new IntegrationNeed("Beeper", "beeper", new IntegrationCapability(CapabilityAction.Send, "message"), IntegrationNeedSource.Catalog);
        var (handler, _, _) = Handler(IntegrationResolution.Missing(beeper), localOnly: true);

        var reply = await Ask(handler, "send a message to my brother on beeper");

        Assert.Equal(ConnectedAppReplyKind.ConnectOffered, reply!.Kind);
        Assert.Contains("nothing goes over the internet", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAppReachedOverTheInternetIsNotOfferedWhileLocalOnlyModeIsOn()
    {
        var (handler, finder, _) = Handler(IntegrationResolution.Missing(CreateTask), localOnly: true);

        var reply = await Ask(handler);

        Assert.Equal(ConnectedAppReplyKind.DiscoveryBlocked, reply!.Kind);
        Assert.Null(reply.Offer);
        Assert.Contains("Local Only mode is on", reply.Text, StringComparison.Ordinal);
        Assert.Equal(0, finder.Asked);
    }

    [Fact]
    public async Task AnAppThatSignsInWithOAuthAndNeedsToSignInAgainIsOfferedASignIn()
    {
        var installed = Sample.Remote("todoist", "Todoist") with
        {
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.Expired },
        };
        var (handler, finder, _) = Handler(IntegrationResolution.NotUsable(CreateTask, installed, InstalledProblem.NeedsSignIn));

        var reply = await Ask(handler);

        Assert.Equal(ConnectedAppReplyKind.ConnectOffered, reply!.Kind);
        Assert.Equal(IntegrationOfferKind.SignIn, reply.Offer!.Kind);
        Assert.Contains("sign in to Todoist again", reply.Text, StringComparison.Ordinal);
        Assert.Equal(0, finder.Asked);
    }

    [Fact]
    public async Task WithoutTheListOfKnownAppsNothingIsConnectedAndTheFinderIsAsked()
    {
        var (handler, finder, _) = Handler(IntegrationResolution.Missing(CreateTask), known: false);

        var reply = await Ask(handler);

        Assert.NotEqual(ConnectedAppReplyKind.ConnectOffered, reply?.Kind);
        Assert.Equal(1, finder.Asked);
    }

    // ---- The approval ----

    [Fact]
    public async Task OnlyTheUsersClickOnTheOfferConnectsAndItIsForThatOfferOnce()
    {
        var connector = new RecordingConnector();
        var offers = new IntegrationOffers(new NoInstaller(), new ManualTimeProvider(), NullLogger<IntegrationOffers>.Instance, connector: connector);
        var offer = await offers.OfferConnectAsync(Todoist, existing: null, CreateTask.Capability);

        Assert.Empty(connector.Calls);
        var none = await offers.AcceptAsync("todoist");
        Assert.Equal(InstallFailure.OfferExpired, none.Failure);

        var outcome = await offers.AcceptAsync(offer.OfferId);
        Assert.True(outcome.IsInstalled);
        Assert.Equal(["connect:todoist"], connector.Calls);
        Assert.Equal(InstallFailure.OfferExpired, (await offers.AcceptAsync(offer.OfferId)).Failure);
    }

    [Fact]
    public async Task ASignInOfferSignsInTheInstalledAppAndATurnedDownOfferDoesNothing()
    {
        var connector = new RecordingConnector();
        var offers = new IntegrationOffers(new NoInstaller(), new ManualTimeProvider(), NullLogger<IntegrationOffers>.Instance, connector: connector);
        var signIn = await offers.OfferConnectAsync(Todoist, Sample.Remote("todoist", "Todoist"), null);
        var declined = await offers.OfferConnectAsync(Todoist, null, null);

        offers.Decline(declined.OfferId);
        Assert.Equal(InstallFailure.OfferExpired, (await offers.AcceptAsync(declined.OfferId)).Failure);
        await offers.AcceptAsync(signIn.OfferId);

        Assert.Equal(["signin:todoist"], connector.Calls);
    }

    // ---- The connector ----

    private sealed class Setup : IAsyncDisposable
    {
        public Setup(FakeOAuthServer server, ReturningBrowser browser, bool localOnly = false, McpFailure? connectFailure = null, Func<int, StubMcpClient>? clients = null)
        {
            Secrets = new FakeSecretStore();
            Clock = new ManualTimeProvider();
            Store = new MemoryIntegrationStore();
            Registry = new InstalledIntegrationRegistry(Store, NullLogger<InstalledIntegrationRegistry>.Instance, Secrets);
            Client = new StubMcpClient();
            Client.Tools.Add(Sample.Tool("create_task", "Creates a task."));
            if (connectFailure is { } failure)
            {
                Client.ConnectFailure = new McpException(failure);
            }

            OAuth = new McpOAuthClient(browser, Clock, server);
            Sessions = new McpOAuthSessions(Secrets, OAuth, Clock);
            var made = 0;
            Manager = new McpConnectionManager(
                Registry, new StubClientFactory(integration => clients is null ? Client : Signed(clients(made++), integration)), TestSettings.LocalOnly(localOnly), Clock, new McpLoadingOptions(),
                NullLogger<McpConnectionManager>.Instance, cache: null, tokens: Sessions);
            Settings = TestSettings.LocalOnly(localOnly);
            Connector = new IntegrationConnector(
                Registry, OAuth, Sessions, Manager, Settings, Secrets, Clock, NullLogger<IntegrationConnector>.Instance);
        }

        public FixedSettings Settings { get; }

        // As the real client factory does, a connection fetches the access token first, which renews it when it has run out.
        private StubMcpClient Signed(StubMcpClient client, InstalledIntegration integration)
        {
            client.BeforeConnect = () => Sessions.GetAccessTokenAsync(integration);
            return client;
        }

        public FakeSecretStore Secrets { get; }

        public ManualTimeProvider Clock { get; }

        public MemoryIntegrationStore Store { get; }

        public InstalledIntegrationRegistry Registry { get; }

        public StubMcpClient Client { get; }

        public McpConnectionManager Manager { get; }

        public McpOAuthClient OAuth { get; }

        public McpOAuthSessions Sessions { get; }

        public IntegrationConnector Connector { get; }

        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            OAuth.Dispose();
        }
    }

    [Fact]
    public async Task ConnectingAnAppWhoseToolsNeedAPermissionTurnsItOn_AndSaysThatItDid()
    {
        // Beeper's tools need Messaging, which is off until the user turns it on: connected without it, the app could do nothing.
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser());
        Assert.False(setup.Settings.Current.Permissions.Messaging);

        var outcome = await setup.Connector.ConnectAsync(Todoist with { Capability = PermissionCapability.Messaging });

        Assert.True(outcome.IsInstalled);
        Assert.True(setup.Settings.Current.Permissions.Messaging);
        Assert.Contains("Messaging was turned on in Settings, under Permissions", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("you are still asked before anything is sent or changed", outcome.Message, StringComparison.Ordinal);

        // Nothing else of the user's choices is touched, and an app that needs no permission, or one that is on already, says nothing of it.
        Assert.False(setup.Settings.Current.Permissions.Calendar);
        await using var plain = new Setup(new FakeOAuthServer(), new ReturningBrowser());
        var quiet = await plain.Connector.ConnectAsync(Todoist);
        Assert.DoesNotContain("turned on", quiet.Message, StringComparison.Ordinal);
        Assert.False(plain.Settings.Current.Permissions.Messaging);
    }

    [Fact]
    public async Task AnAppThatCouldNotBeSignedInToTurnsNothingOn()
    {
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser { Error = "access_denied" });

        var outcome = await setup.Connector.ConnectAsync(Todoist with { Capability = PermissionCapability.Messaging });

        Assert.False(outcome.IsInstalled);
        Assert.False(setup.Settings.Current.Permissions.Messaging);
    }

    [Fact]
    public async Task ConnectingRecordsTheAppSignsInKeepsTheTokensAndReadsTheTools()
    {
        var server = new FakeOAuthServer();
        await using var setup = new Setup(server, new ReturningBrowser());

        var outcome = await setup.Connector.ConnectAsync(Todoist);

        Assert.True(outcome.IsInstalled);
        Assert.Equal(["create_task"], outcome.ToolNames);
        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal("todoist", record.Id);
        Assert.Equal(IntegrationAuthKind.OAuth, record.Authentication.Kind);
        Assert.Equal(IntegrationAuthState.Ready, record.Authentication.State);
        Assert.True(record.Enabled);
        Assert.True(record.Permissions.LeavesThisPc);
        Assert.Equal(IntegrationSourceKind.Bundled, record.Source.Kind);
        Assert.NotEmpty(record.Authentication.Secrets);
        Assert.All(record.Authentication.Secrets, binding => Assert.True(setup.Secrets.Secrets.ContainsKey(binding.SecretName)));
        Assert.Equal(["create_task"], record.Capabilities.ToolNames);

        // No token is in the record, and the one that was kept is the one the server gave.
        Assert.DoesNotContain("AT1", System.Text.Json.JsonSerializer.Serialize(record), StringComparison.Ordinal);
        Assert.Equal("AT1", await setup.Sessions.GetAccessTokenAsync(record));
    }

    [Fact]
    public async Task ConnectingToAnAppReachedOverTheInternetIsRefusedWhileLocalOnlyModeIsOn()
    {
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser(), localOnly: true);

        var outcome = await setup.Connector.ConnectAsync(Todoist);

        Assert.Equal(InstallFailure.WebLocked, outcome.Failure);
        Assert.Empty(setup.Store.Saved);
        Assert.Empty(setup.Secrets.Secrets);
    }

    [Fact]
    public async Task PipedreamConnectsWithRootMetadataAndKeepsOnlyCredentialReferences()
    {
        // As Pipedream's own documents are: the server is at /v2, and calls itself the resource https://mcp.pipedream.net, which is the only one its
        // authorization page accepts (asked for the address, it answers "Invalid or unauthorized resource parameter").
        var server = new FakeOAuthServer
        {
            AuthorizationServer = "https://mcp.pipedream.com", RootMetadataHost = "mcp.pipedream.net", RootResource = "https://mcp.pipedream.net",
        };
        var browser = new ReturningBrowser();
        await using var setup = new Setup(server, browser);
        var endpoint = KnownEndpoints.For("microsofttodopipedream")!;

        var outcome = await setup.Connector.ConnectAsync(endpoint);

        Assert.True(outcome.IsInstalled);
        Assert.Equal("mcp offline_access", browser.OpenedQuery!["scope"]);
        Assert.Equal("https://mcp.pipedream.net", browser.OpenedQuery["resource"]);
        Assert.Equal("https://mcp.pipedream.net", server.TokenRequests.Single()["resource"]);
        Assert.Equal("S256", browser.OpenedQuery["code_challenge_method"]);
        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal(endpoint.IntegrationId, record.Id);
        Assert.Equal("Microsoft To Do", record.Name);
        Assert.Equal(IntegrationAuthState.Ready, record.Authentication.State);
        Assert.Equal(IntegrationHealthStatus.Healthy, record.Health.Status);
        Assert.Equal(["create_task"], record.Capabilities.ToolNames);
        Assert.DoesNotContain("AT1", System.Text.Json.JsonSerializer.Serialize(record), StringComparison.Ordinal);
        Assert.DoesNotContain("RT1", System.Text.Json.JsonSerializer.Serialize(record), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomServerUsesOAuthAndIsRecordedAsUserAdded()
    {
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser());
        var endpoint = new KnownEndpoint("customnotes", "Custom notes", "https://mcp.example.com/mcp", "mcp.example.com");

        var outcome = await setup.Connector.ConnectAsync(endpoint);

        Assert.True(outcome.IsInstalled);
        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal(IntegrationSourceKind.UserAdded, record.Source.Kind);
        Assert.Equal(IntegrationAuthState.Ready, record.Authentication.State);
        Assert.Equal(endpoint.Endpoint, record.Transport.Endpoint);
        Assert.Empty(IntegrationRules.Problems(record));
    }

    [Fact]
    public async Task AnAppOnThisPcThatIsNotRunningIsSaidSoAndNothingIsRecorded()
    {
        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser());
        var beeper = new KnownEndpoint("beeper", "Beeper", $"http://127.0.0.1:{port}/v0/mcp", "beeper.com", RunsOnThisPc: true, SignInHelp: "Turn on Beeper Desktop API.");

        var outcome = await setup.Connector.ConnectAsync(beeper);

        Assert.Equal(InstallFailure.AppNotRunning, outcome.Failure);
        Assert.Contains("Turn on Beeper Desktop API.", outcome.Message, StringComparison.Ordinal);
        Assert.Empty(setup.Store.Saved);
    }

    [Fact]
    public async Task ASignInThatTheUserDeniedLeavesTheAppRecordedAsNeedingOneAndNoToken()
    {
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser { Error = "access_denied" });

        var outcome = await setup.Connector.ConnectAsync(Todoist);

        Assert.Equal(InstallFailure.SignInFailed, outcome.Failure);
        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal(IntegrationAuthState.NeedsSignIn, record.Authentication.State);
        Assert.Empty(setup.Secrets.Secrets);
    }

    [Fact]
    public async Task SigningInAgainReplacesTheTokensAndSigningOutDeletesThem()
    {
        var server = new FakeOAuthServer();
        await using var setup = new Setup(server, new ReturningBrowser());
        await setup.Connector.ConnectAsync(Todoist);

        var again = await setup.Connector.SignInAsync("todoist");
        Assert.True(again.IsInstalled);
        Assert.Equal(2, server.TokenRequests.Count);

        await setup.Connector.SignOutAsync("todoist");

        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal(IntegrationAuthState.NeedsSignIn, record.Authentication.State);
        Assert.Empty(record.Authentication.Secrets);
        Assert.Empty(setup.Secrets.Secrets);
    }

    [Fact]
    public async Task AnAccessTokenTheUserMadeIsKeptAsABearerTokenAndTheOAuthSignInIsForgotten()
    {
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser());
        await setup.Connector.ConnectAsync(Todoist);

        var bad = await setup.Connector.UseTokenAsync("todoist", "has a space");
        var outcome = await setup.Connector.UseTokenAsync("todoist", "  abc123  ");

        Assert.Equal(InstallFailure.NotAllowed, bad.Failure);
        Assert.True(outcome.IsInstalled);
        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal(IntegrationAuthKind.BearerToken, record.Authentication.Kind);
        Assert.Equal("abc123", setup.Secrets.Secrets["todoist.token"]);
        Assert.DoesNotContain(setup.Secrets.Secrets.Keys, name => name.Contains(".oauth.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskingAgainAfterTheAppIsConnectedLeavesTheRequestToTheModelWithTheAppsTools()
    {
        var server = new FakeOAuthServer();
        await using var setup = new Setup(server, new ReturningBrowser());
        var resolver = new IntegrationResolver(
            setup.Registry, setup.Manager, TestSettings.LocalOnly(false), IntegrationRequestReader.Instance, [], null, setup.Clock, new IntegrationResolverOptions(),
            NullLogger<IntegrationResolver>.Instance);
        var offers = new IntegrationOffers(new NoInstaller(), setup.Clock, NullLogger<IntegrationOffers>.Instance, connector: setup.Connector);
        var handler = new ConnectedAppRequestHandler(
            resolver, new NeverFinder(), setup.Clock, NullLogger<ConnectedAppRequestHandler>.Instance, offers: offers, settings: TestSettings.LocalOnly(false),
            knownEndpoints: app => app == "todoist" ? Todoist : null);

        var first = await Ask(handler);
        Assert.Equal(ConnectedAppReplyKind.ConnectOffered, first!.Kind);
        var outcome = await offers.AcceptAsync(first.Offer!.OfferId);
        Assert.True(outcome.IsInstalled);

        // The request goes on: nothing more is offered, and the app is there for the model to use.
        Assert.Null(await Ask(handler));
        var again = await resolver.ResolveRequestAsync("Add milk to Todoist");
        Assert.Equal(IntegrationResolutionKind.UseInstalled, again.Kind);
        Assert.Contains("create_task", again.ToolNames);

        // And when the sign-in has run out, it is a sign-in that is offered, not a search.
        await setup.Registry.RecordAuthenticationStateAsync("todoist", IntegrationAuthState.Expired, setup.Clock.GetUtcNow());
        var expired = await Ask(handler);
        Assert.Equal(IntegrationOfferKind.SignIn, expired!.Offer!.Kind);
    }

    private static StubMcpClient Refusing()
    {
        var client = new StubMcpClient { ConnectFailure = new McpException(McpFailure.AuthRequired) { OffersSignIn = true } };
        return client;
    }

    private static StubMcpClient Working()
    {
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool("create_task", "Creates a task."));
        return client;
    }

    [Fact]
    public async Task ARefusedAccessTokenIsRenewedOnceWithTheRefreshTokenBeforeTheUserIsAskedToSignInAgain()
    {
        var server = new FakeOAuthServer();
        await using var setup = new Setup(server, new ReturningBrowser(), clients: attempt => attempt == 0 ? Working() : attempt == 1 ? Refusing() : Working());
        await setup.Connector.ConnectAsync(Todoist);
        await setup.Manager.ForgetAsync("todoist", CancellationToken.None);

        // The next connection is refused once (the first stub the manager makes after the check), and a renewed token gets through.
        var catalog = await setup.Manager.GetCatalogAsync("todoist", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(catalog);
        Assert.Contains(server.TokenRequests, request => request["grant_type"] == "refresh_token" && request["refresh_token"] == "RT1");
        Assert.Equal(IntegrationAuthState.Ready, Assert.Single(setup.Store.Saved).Authentication.State);
    }

    [Fact]
    public async Task ARefusedAccessTokenThatCannotBeRenewedIsASignInTheUserHasToMake()
    {
        var server = new FakeOAuthServer();
        await using var setup = new Setup(server, new ReturningBrowser(), clients: attempt => attempt == 0 ? Working() : Refusing());
        await setup.Connector.ConnectAsync(Todoist);
        await setup.Manager.ForgetAsync("todoist", CancellationToken.None);
        await setup.Sessions.SaveAsync(
            "todoist", new OAuthSignIn(new Uri("https://auth.example.com/token"), "client-1", new Uri("https://mcp.example.com/mcp"), new OAuthTokens("AT-old", null, null, null)),
            CancellationToken.None);

        var catalog = await setup.Manager.GetCatalogAsync("todoist", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Null(catalog);
        Assert.Equal(IntegrationAuthState.NeedsSignIn, Assert.Single(setup.Store.Saved).Authentication.State);
        Assert.DoesNotContain(server.TokenRequests, request => request["grant_type"] == "refresh_token");
    }

    [Fact]
    public async Task KeysAreKeptInTheSecretStoreAndTheAppIsReadyOnlyWhenEveryKeyIsThere()
    {
        await using var setup = new Setup(new FakeOAuthServer(), new ReturningBrowser());
        await setup.Registry.AddAsync(Sample.Remote("acme", "Acme") with
        {
            Authentication = new IntegrationAuthentication
            {
                Kind = IntegrationAuthKind.HeaderKey,
                State = IntegrationAuthState.NeedsSignIn,
                Secrets = [new IntegrationSecretBinding("X-Api-Key", "acme.key"), new IntegrationSecretBinding("X-Team", "acme.team")],
            },
        });

        var bad = await setup.Connector.SetKeyAsync("acme", "X-Api-Key", "  ");
        var unknown = await setup.Connector.SetKeyAsync("acme", "Other", "value");
        var first = await setup.Connector.SetKeyAsync("acme", "X-Api-Key", " key-1 ");

        Assert.Equal(InstallFailure.NotAllowed, bad.Failure);
        Assert.Equal(InstallFailure.NotAllowed, unknown.Failure);
        Assert.True(first.IsInstalled);
        Assert.Contains("still needs its other keys", first.Message, StringComparison.Ordinal);
        Assert.Equal("key-1", setup.Secrets.Secrets["acme.key"]);
        Assert.Equal(IntegrationAuthState.NeedsSignIn, Assert.Single(setup.Store.Saved).Authentication.State);

        var second = await setup.Connector.SetKeyAsync("acme", "X-Team", "team-9");

        Assert.True(second.IsInstalled);
        Assert.Equal(["create_task"], second.ToolNames);
        var record = Assert.Single(setup.Store.Saved);
        Assert.Equal(IntegrationAuthState.Ready, record.Authentication.State);
        Assert.DoesNotContain("key-1", System.Text.Json.JsonSerializer.Serialize(record), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryKnownEndpointIsAnAddressTheAssistantAccepts()
    {
        Assert.NotEmpty(KnownEndpoints.All);
        Assert.All(KnownEndpoints.All, endpoint => Assert.True(KnownEndpoints.IsValid(endpoint), endpoint.AppKey));
        Assert.All(KnownEndpoints.All, endpoint => Assert.True(IntegrationRules.IsValidId(endpoint.IntegrationId)));
        Assert.All(KnownEndpoints.All, endpoint => Assert.NotNull(KnownApps.ByAppKey(endpoint.AppKey)));
        Assert.True(KnownEndpoints.For("beeper")!.RunsOnThisPc);
    }
}
