using Assistant.Tools.Mcp;

namespace Assistant.Tools.Integrations;

/// <summary>
/// An integration for an app that is already on this PC but not installed in the Assistant: a server another program has been set up with. It holds
/// only what is needed to say so to the user. It never holds a program's path or arguments, an address, an environment variable or a key, and
/// nothing about it is run, connected to or imported.
/// </summary>
/// <param name="Name">The name the other program gives the server, cleaned.</param>
/// <param name="Where">The program that has it, in words the user knows ("Claude Desktop", "VS Code").</param>
/// <param name="Kind">How that program reaches it: <see cref="McpTransportKind.Stdio"/> for a program it starts, otherwise a server it connects to.</param>
public sealed record AvailableIntegration(string Name, string Where, McpTransportKind Kind);

/// <summary>
/// Looks for an integration for an app that the user already has on this PC but has not installed in the Assistant (PROJECT_SPEC §4.8, step 105): the second place
/// the resolver looks, after the installed integrations and before anything is looked up on the web. Finding one does not install it; it only
/// stops the Assistant from going to find another.
/// </summary>
public interface IAvailableIntegrationSource
{
    /// <summary>The integrations the source knows of that are about the app <paramref name="appKey"/> (<see cref="AppIdentity"/>); empty when there are none or the source cannot say.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<IReadOnlyList<AvailableIntegration>> FindAsync(string appKey, CancellationToken cancellationToken = default);
}
