using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// Keeps some tools of a connected app from being offered to the model (PROJECT_SPEC §4.8, step 116). A messaging app's own tool that sends to a chat by id would let the model message
/// someone the user never saved, so while the Assistant sends messages itself (<c>send_message</c>, to saved people only) the app's own sending tools are the Assistant's to use and
/// are not offered. The tools stay installed and known: only what the model is offered, and so what it may call, changes.
/// </summary>
internal interface IMcpToolVeto
{
    /// <summary>
    /// The server's names (<see cref="McpToolDescriptor.Name"/>) of the tools of <paramref name="integration"/> that are not to be offered now. A veto that cannot tell says none.
    /// </summary>
    /// <param name="integration">The app.</param>
    /// <param name="tools">What the app's tools are, as they could be offered.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    Task<IReadOnlySet<string>> ReservedToolsAsync(InstalledIntegration integration, IReadOnlyList<McpTool> tools, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="ReservedToolsAsync(InstalledIntegration, IReadOnlyList{McpTool}, CancellationToken)"/>, for the request in <paramref name="context"/>: a veto
    /// may keep more from the model for one request than for another (every tool of a messaging app while a message is being sent through it). Without an
    /// override, the same whatever is asked.
    /// </summary>
    Task<IReadOnlySet<string>> ReservedToolsAsync(
        InstalledIntegration integration, IReadOnlyList<McpTool> tools, Assistant.Core.Contracts.ToolContext context, CancellationToken cancellationToken) =>
        ReservedToolsAsync(integration, tools, cancellationToken);
}
