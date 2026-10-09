using System.Diagnostics;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Assistant.Tools.Calculator;
using Assistant.Tools.Integrations;
using Assistant.Tools.Mcp;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Tools.Tests.Mcp;

/// <summary>A permission policy that says no to the capabilities a test names, and yes to the rest.</summary>
internal sealed class DenyingPermissions(params PermissionCapability[] denied) : IPermissionPolicy
{
    public List<PermissionCapability> Asked { get; } = [];

    public Task<PermissionDecision> CheckAsync(PermissionCapability capability, CancellationToken cancellationToken = default)
    {
        Asked.Add(capability);
        return Task.FromResult(new PermissionDecision(
            capability, denied.Contains(capability) ? PermissionDecisionReason.TurnedOff : PermissionDecisionReason.Granted));
    }
}

/// <summary>
/// The whole of the connected apps as the app wires them, with stub clients in place of servers: the registry, the connection manager, the
/// source that loads tools by the request, the tool registry and the executor.
/// </summary>
internal sealed class ConnectedAppsFixture : IAsyncDisposable
{
    public ConnectedAppsFixture(
        IEnumerable<InstalledIntegration> integrations,
        Func<InstalledIntegration, StubMcpClient>? clients = null,
        TimeProvider? clock = null,
        McpLoadingOptions? options = null,
        bool localOnly = false,
        bool confirm = true,
        IPermissionPolicy? permissions = null,
        MemoryIntegrationStore? store = null,
        IMcpToolCache? cache = null)
    {
        Store = store ?? new MemoryIntegrationStore([.. integrations]);
        Integrations = new InstalledIntegrationRegistry(Store, NullLogger<InstalledIntegrationRegistry>.Instance);
        Clients = new StubClientFactory(clients ?? (_ => Todoist()));
        Clock = clock ?? TimeProvider.System;
        Options = options ?? new McpLoadingOptions();
        Settings = TestSettings.LocalOnly(localOnly);
        Manager = new McpConnectionManager(Integrations, Clients, Settings, Clock, Options, NullLogger<McpConnectionManager>.Instance, cache);
        Source = new McpToolSource(Integrations, Manager, LexicalMcpToolSelector.Instance, Options, Clock, NullLogger<McpToolSource>.Instance);
        Confirmation = new FakeConfirmation(confirm);
        Permissions = permissions ?? new FakePermissions(true);
        ITool[] builtIn = [CalculateTool.Create()];
        Tools = new ToolRegistry(builtIn, Source);
        Executor = new ToolExecutor(builtIn, Confirmation, Permissions, Source);
    }

    public MemoryIntegrationStore Store { get; }

    public InstalledIntegrationRegistry Integrations { get; }

    public StubClientFactory Clients { get; }

    public TimeProvider Clock { get; }

    public McpLoadingOptions Options { get; }

    public FixedSettings Settings { get; }

    public McpConnectionManager Manager { get; }

    public McpToolSource Source { get; }

    public FakeConfirmation Confirmation { get; }

    public IPermissionPolicy Permissions { get; }

    public ToolRegistry Tools { get; }

    public ToolExecutor Executor { get; }

    /// <summary>A to-do app: a task is created (a change), tasks are listed (a read), and everything can be deleted.</summary>
    public static StubMcpClient Todoist()
    {
        var client = new StubMcpClient();
        client.Tools.Add(Sample.Tool(
            "createTask", "Creates a task in the to-do list.",
            """{"type":"object","properties":{"taskTitle":{"type":"string","description":"The title."},"dueDate":{"type":"string"}},"required":["taskTitle"]}"""));
        client.Tools.Add(Sample.Tool("listTasks", "Lists the tasks in the to-do list.", """{"type":"object","properties":{"status":{"type":"string","enum":["open","done"]}}}"""));
        client.Tools.Add(Sample.Tool("deleteAllTasks", "Deletes every task.", """{"type":"object"}""", destructive: true));
        return client;
    }

    /// <summary>The to-do app, installed with its listing tool vetted as read-only.</summary>
    public static InstalledIntegration TodoistApp(Func<IntegrationPermissions, IntegrationPermissions>? permissions = null) =>
        Sample.Remote() with { Permissions = (permissions ?? (current => current))(new IntegrationPermissions { ReadOnlyTools = ["listTasks"] }) };

    public static ToolContext Context(string request, Guid? conversation = null) => new(conversation ?? Guid.NewGuid(), request);

    public async Task<IReadOnlyList<ToolDefinition>> OfferedAsync(ToolContext context)
    {
        await Tools.PrepareToolsAsync(context);
        return Tools.ToolsFor(context);
    }

    public Task<ToolResult> CallAsync(ToolContext context, string tool, string arguments) =>
        Executor.ExecuteAsync(new ToolCall("call_" + Guid.NewGuid().ToString("N")[..6], tool, arguments), context);

    public ValueTask DisposeAsync() => Manager.DisposeAsync();

    /// <summary>Waits for what happens in the background.</summary>
    public static async Task EventuallyAsync(Func<bool> condition, int milliseconds = 10_000)
    {
        var until = Stopwatch.StartNew();
        while (!condition())
        {
            if (until.ElapsedMilliseconds > milliseconds)
            {
                throw new Xunit.Sdk.XunitException("The condition was not met in time.");
            }

            await Task.Delay(10);
        }
    }
}
