using System.Diagnostics;
using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Memory;
using Assistant.Core.People;
using Assistant.Core.Tools;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Assistant.Tools.Mcp.Auth;
using Assistant.Tools.Messaging;
using Assistant.Tools.Messaging.ConnectedApps;
using Assistant.Tools.Tests.Mcp;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tools.Tests.Live;

/// <summary>A test that runs only when the environment variable it names is "1": it signs in to a real app in the user's browser and acts there.</summary>
internal sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
        {
            Skip = $"A live check: set {variable}=1 to run it.";
        }
    }
}

/// <summary>
/// The connected apps, for real (RELEASE_CHECKLIST.md): the sign-in opens in the browser for the person running it to approve, what it gives is kept in
/// memory for the test alone (never in Credential Manager or a file), and the Assistant's own code then does what a request would. Nothing here runs unless
/// asked for by its variable. Who is messaged and what is said come from variables too (<c>KIRI_LIVE_TO</c>, <c>KIRI_LIVE_RELATION</c>,
/// <c>KIRI_LIVE_TEXT</c>), and what happened is written to <c>KIRI_LIVE_OUT</c>.
/// </summary>
public sealed class LiveConnectedAppTests
{
    private static readonly TimeSpan SignInTime = TimeSpan.FromMinutes(9);

    private static string Out => Environment.GetEnvironmentVariable("KIRI_LIVE_OUT") ?? Path.Combine(Path.GetTempPath(), "kiri-live.txt");

    private static void Say(string line) => File.AppendAllText(Out, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");

    private sealed class MemorySecrets : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task SetAsync(string name, string secret, CancellationToken cancellationToken = default)
        {
            lock (_secrets)
            {
                _secrets[name] = secret;
            }

            return Task.CompletedTask;
        }

        public Task<string?> GetAsync(string name, CancellationToken cancellationToken = default)
        {
            lock (_secrets)
            {
                return Task.FromResult(_secrets.GetValueOrDefault(name));
            }
        }

        public Task<bool> DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            lock (_secrets)
            {
                return Task.FromResult(_secrets.Remove(name));
            }
        }
    }

    // The user's own browser, as the app's sign-in opens it.
    private sealed class ShellBrowser : IOAuthBrowser
    {
        public Task<bool> OpenAsync(Uri address, CancellationToken cancellationToken = default)
        {
            Say("The sign-in page is open in the browser: " + address.GetLeftPart(UriPartial.Path));
            using var started = Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true });
            return Task.FromResult(true);
        }
    }

    // The user says yes to what they are asked, as they would on the card: the question is written down first.
    private sealed class SayYes : IPermissionService
    {
        public List<ToolConfirmation> Shown { get; } = [];

        public Task<ConfirmationDecision> ConfirmToolCallAsync(
            ToolDefinition tool, ToolCall call, ToolContext context, ToolConfirmation confirmation, CancellationToken cancellationToken = default)
        {
            Shown.Add(confirmation);
            Say($"Asked: {confirmation.Title} " + string.Join("; ", confirmation.Details.Select(detail => $"{detail.Label}: {detail.Value}")) + " -> Send");
            return Task.FromResult(ConfirmationDecision.Approved);
        }
    }

    private sealed record Connection(InstalledIntegrationRegistry Registry, McpConnectionManager Manager, McpOAuthClient OAuth) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Manager.DisposeAsync();
            OAuth.Dispose();
        }
    }

    // Connects the known app as the app's connector does, with the sign-in kept in memory.
    private static async Task<Connection> ConnectAsync(KnownEndpoint endpoint)
    {
        var secrets = new MemorySecrets();
        var registry = new InstalledIntegrationRegistry(new MemoryIntegrationStore([]), NullLogger<InstalledIntegrationRegistry>.Instance, secrets);
        var oauth = new McpOAuthClient(new ShellBrowser());
        var sessions = new McpOAuthSessions(secrets, oauth);
        await registry.AddAsync(new InstalledIntegration
        {
            Id = endpoint.IntegrationId,
            Name = endpoint.Name,
            Source = new IntegrationSource(IntegrationSourceKind.Bundled, new Uri(endpoint.Endpoint).Host),
            Transport = new IntegrationTransport { Kind = McpTransportKind.StreamableHttp, Endpoint = endpoint.Endpoint },
            Enabled = true,
            Authentication = new IntegrationAuthentication { Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.NeedsSignIn },
            Permissions = new IntegrationPermissions { LeavesThisPc = !endpoint.RunsOnThisPc, RequiredCapability = endpoint.Capability },
        });

        Say($"Signing in to {endpoint.Name}: approve it in the browser.");
        var signIn = await oauth.SignInAsync(new Uri(endpoint.Endpoint), "Assistant (test)", endpoint.Scope, SignInTime, CancellationToken.None);
        var bindings = await sessions.SaveAsync(endpoint.IntegrationId, signIn, CancellationToken.None);
        await registry.UpdateAsync(
            endpoint.IntegrationId,
            current => current with
            {
                Authentication = new IntegrationAuthentication
                {
                    Kind = IntegrationAuthKind.OAuth, State = IntegrationAuthState.Ready, Secrets = bindings, ExpiresAt = signIn.Tokens.ExpiresAt, CheckedAt = DateTimeOffset.UtcNow,
                },
            });
        Say($"Signed in to {endpoint.Name}.");
        var manager = new McpConnectionManager(
            registry, new McpClientFactory(secrets, new McpClientOptions(), sessions), TestSettings.LocalOnly(false), TimeProvider.System, new McpLoadingOptions(),
            NullLogger<McpConnectionManager>.Instance, cache: null, tokens: sessions);
        return new Connection(registry, manager, oauth);
    }

    private static string Required(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : throw new InvalidOperationException($"Set {variable}.");

    [LiveFact("KIRI_LIVE_BEEPER")]
    public async Task Beeper_TheAssistantsOwnToolsFindThePersonAndSendTheTestMessage()
    {
        var name = Required("KIRI_LIVE_TO");
        var relation = Environment.GetEnvironmentVariable("KIRI_LIVE_RELATION") ?? string.Empty;
        var text = Environment.GetEnvironmentVariable("KIRI_LIVE_TEXT") ?? "claude testing ignore";
        await using var connection = await ConnectAsync(KnownEndpoints.For("beeper")!);

        var catalog = await connection.Manager.GetCatalogAsync("beeper", TimeSpan.FromSeconds(20), CancellationToken.None);
        Assert.NotNull(catalog);
        Say("Beeper's tools: " + string.Join(", ", catalog.Tools.Select(tool => tool.Descriptor.Name)));

        var provider = new McpMessagingProvider(
            connection.Registry, connection.Manager, new FakePermissions(true), NullLogger<McpMessagingProvider>.Instance, new InMemoryMemoryStore());
        var integration = (await connection.Registry.GetAsync("beeper"))!;
        Say("Kept from the model: " + string.Join(", ", (await provider.ReservedToolsAsync(integration, catalog.Tools, CancellationToken.None)).Order()));

        // The person as the app keeps them once the user has said who they are: their name as their chat's, and how they relate to the user.
        var people = new InMemoryPersonStore();
        await people.SaveAsync(Person.Create(name, DateTimeOffset.UtcNow) with
        {
            Relationships = relation.Length > 0 ? [relation] : [],
            Identifiers = [new PersonIdentifier(PersonIdentifierKind.ChatName, name)],
        });
        var draft = new DraftMessageTool(provider, new PersonResolver(people));
        var send = new SendMessageTool(provider, new PersonResolver(people));
        var asked = new SayYes();
        var executor = new ToolExecutor([draft, send], asked, new FakePermissions(true));
        var recipient = relation.Length > 0 ? "my " + relation : name;
        var context = new ToolContext(Guid.NewGuid(), $"send a message to {recipient} on beeper saying {text}");
        var registry = new ToolRegistry([draft, send]);
        await registry.PrepareToolsAsync(context);
        Say("Offered for the request: " + string.Join(", ", registry.ToolsFor(context).Select(tool => tool.Name)));

        // Where it would go, first: a person with more than one chat is a question, answered with the service the request named.
        var arguments = new Dictionary<string, string> { ["recipient"] = recipient, ["text"] = text };
        var drafted = await executor.ExecuteAsync(new ToolCall("c1", "draft_message", JsonSerializer.Serialize(arguments)), context);
        Say($"draft_message: {drafted.Status} {drafted.OutputJson}");
        if (drafted.Status != ToolResultStatus.Succeeded || drafted.OutputJson.Contains("Beeper", StringComparison.Ordinal) && drafted.OutputJson.Contains("more than one chat", StringComparison.Ordinal))
        {
            arguments["via"] = "Beeper";
            drafted = await executor.ExecuteAsync(new ToolCall("c2", "draft_message", JsonSerializer.Serialize(arguments)), context);
            Say($"draft_message via Beeper: {drafted.Status} {drafted.OutputJson}");
        }

        Assert.Equal(ToolResultStatus.Succeeded, drafted.Status);
        Assert.Contains("\"status\":\"drafted\"", drafted.OutputJson, StringComparison.Ordinal);

        var sent = await executor.ExecuteAsync(new ToolCall("c3", "send_message", JsonSerializer.Serialize(arguments)), context);
        Say($"send_message: {sent.Status} {sent.OutputJson}");
        Assert.Equal(ToolResultStatus.Succeeded, sent.Status);
        Assert.Single(asked.Shown);
        Assert.Equal(text, asked.Shown[0].Details.Single(detail => detail.Label == "Message").Value);
    }

    [LiveFact("KIRI_LIVE_TODO")]
    public async Task MicrosoftToDoThroughPipedream_SignsIn_AndItsToolsAreOfferedForARequestAboutATask()
    {
        await using var connection = await ConnectAsync(KnownEndpoints.For("microsofttodopipedream")!);
        var id = "microsofttodopipedream";

        var catalog = await connection.Manager.GetCatalogAsync(id, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(catalog);
        foreach (var tool in catalog.Tools)
        {
            Say($"To Do tool: {tool.Descriptor.Name} — {tool.Definition.InputSchemaJson}");
        }

        var source = new McpToolSource(
            connection.Registry, connection.Manager, LexicalMcpToolSelector.Instance, new McpLoadingOptions(), TimeProvider.System, NullLogger<McpToolSource>.Instance, []);
        foreach (var request in new[] { "add homework to my to do, set it due today", "add buy milk to my to do", "add homework to Microsoft To Do" })
        {
            var context = new ToolContext(Guid.NewGuid(), request);
            await source.PrepareAsync(context, CancellationToken.None);
            Say($"Offered for \"{request}\": " + string.Join(", ", source.Offered(context).Select(tool => tool.Definition.Name)));
        }

        Assert.NotEmpty(catalog.Tools);
    }
}
