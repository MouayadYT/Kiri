using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Mcp;

/// <summary>Calls a tool of a connected app, connecting to it first if it is not connected.</summary>
internal interface IMcpToolInvoker
{
    /// <summary>Calls <paramref name="tool"/> of the integration <paramref name="integrationId"/> with <paramref name="arguments"/>, by the names the server gave the arguments.</summary>
    /// <param name="integrationId">The app.</param>
    /// <param name="tool">The tool.</param>
    /// <param name="arguments">The arguments, by the names the server gave them.</param>
    /// <param name="safeToRepeat">
    /// Whether the call may be made again if the program stopped while it was being made (a tool that only reads). A call that may have changed something
    /// is never repeated: it was perhaps done, and doing it twice could do it twice.
    /// </param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <exception cref="McpException">The integration cannot be used now or the call failed.</exception>
    Task<McpToolResult> CallAsync(string integrationId, McpToolDescriptor tool, JsonElement arguments, bool safeToRepeat, CancellationToken cancellationToken);
}

/// <summary>
/// One tool of a connected app, as an <see cref="ITool"/> of the typed tool registry (PROJECT_SPEC §4.8, step 104). It is run by the one executor
/// like any other tool: its permission is checked, its arguments are checked against its schema, a call is confirmed by the user unless the tool
/// is known to only read, and it has a time limit. What differs is where the work is done: the arguments, named as the model wrote them (lower
/// snake_case), are put back under the names the server gave them and sent to the app, and what comes back is data (<see cref="McpToolResults"/>).
/// </summary>
internal sealed class McpTool : ITool
{
    private readonly IMcpToolInvoker _invoker;
    private readonly IReadOnlyDictionary<string, string> _serverNames;
    private readonly bool _readsEvents;

    /// <summary>Creates the tool.</summary>
    /// <param name="integrationId">The app.</param>
    /// <param name="appName">The app's name, cleaned, as the model and the user are told it.</param>
    /// <param name="descriptor">The tool as the server listed it.</param>
    /// <param name="definition">The tool as the registry knows it.</param>
    /// <param name="serverNames">For each argument of <paramref name="definition"/>, the name the server gave it.</param>
    /// <param name="invoker">What calls the app.</param>
    /// <param name="readsEvents">Whether the tool reads a calendar (step 116): what it returns is checked for exams, and the Assistant's reading is put beside it.</param>
    public McpTool(
        string integrationId,
        string appName,
        McpToolDescriptor descriptor,
        ToolDefinition definition,
        IReadOnlyDictionary<string, string> serverNames,
        IMcpToolInvoker invoker,
        bool readsEvents = false)
    {
        IntegrationId = integrationId;
        AppName = appName;
        Descriptor = descriptor;
        Definition = definition;
        _serverNames = serverNames;
        _invoker = invoker;
        _readsEvents = readsEvents;
    }

    /// <summary>The app the tool belongs to.</summary>
    public string IntegrationId { get; }

    /// <summary>The app's name.</summary>
    public string AppName { get; }

    /// <summary>The tool as the server listed it.</summary>
    public McpToolDescriptor Descriptor { get; }

    /// <inheritdoc/>
    public ToolDefinition Definition { get; }

    /// <inheritdoc/>
    /// <remarks>
    /// A connected app's tool that is not known to only read is no different from a built-in one: the user is asked before it runs (step 115), and what they are shown is
    /// the app, the tool by the name the app gave it, and every argument as it was given, which is what the app is sent. What the app says about the tool (its description, its
    /// own claims of being safe) is the app's words and is not put in the question.
    /// </remarks>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolPlan.Do(
            token => RunAsync(call, arguments, context, token),
            ToolConfirmations.ForCall(Definition, arguments, ConfirmationKind.ConnectedApp, AppName, Descriptor.Name)));

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        // The arguments are put back under the names the server knows them by.
        var forServer = new JsonObject();
        foreach (var argument in arguments.EnumerateObject())
        {
            forServer[_serverNames.TryGetValue(argument.Name, out var serverName) ? serverName : argument.Name] = JsonNode.Parse(argument.Value.GetRawText());
        }

        try
        {
            using var document = JsonDocument.Parse(forServer.ToJsonString());
            var result = await _invoker.CallAsync(IntegrationId, Descriptor, document.RootElement, Definition.RiskLevel == RiskLevel.ReadOnly, cancellationToken).ConfigureAwait(false);
            return McpToolResults.Map(call, AppName, result, _readsEvents);
        }
        catch (McpException exception)
        {
            return FailureResult(call, AppName, exception);
        }
    }

    // What a failure is told to the model: in words that say what to do about it, and with the code the executor and the orchestrator read.
    private static ToolResult FailureResult(ToolCall call, string app, McpException exception)
    {
        switch (exception.Failure)
        {
            case McpFailure.Blocked:
                return ToolErrors.Result(
                    call, ToolResultStatus.Failed, ToolErrors.PermissionOff,
                    $"{app} cannot be used right now: it is turned off, or Local Only mode (Settings, Privacy) is on. Tell the user.");
            case McpFailure.AuthRequired or McpFailure.Forbidden:
                return ToolErrors.Result(
                    call, ToolResultStatus.Failed, ToolErrors.Failed, $"{app} needs the user to sign in again, or refused the request. Tell the user.");
            case McpFailure.TimedOut:
                return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.TimedOut, $"{app} did not answer in time, so the call was given up.");
            case McpFailure.Server:
                var words = McpText.Clean(exception.ServerMessage, McpToolResults.MaxErrorLength);
                return ToolErrors.Result(
                    call, ToolResultStatus.Failed, ToolErrors.Failed, words is null ? $"{app} reported an error." : $"{app} reported an error: {words}");
            case McpFailure.Unsupported:
                return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, $"{app} asked for something this version cannot do.");
            case McpFailure.TooLarge:
                return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.ResultTooLarge, $"{app} sent more than can be given to you. Ask for less.");
            default:
                return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, $"{app} could not be reached, or did not answer properly. Tell the user.");
        }
    }
}
