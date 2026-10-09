using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// Decides which connected apps and which of their tools are worth offering for a request (PROJECT_SPEC §4.8, step 104). It is deliberately
/// not the model: the model has a small window and nothing to choose with before it has seen the tools, and a request that has nothing to do
/// with an app must cost it nothing, not even a connection. Both steps are cheap and need no connection to be answered about an app that is
/// known (its name and the names of its tools are kept with it).
/// </summary>
internal interface IMcpToolSelector
{
    /// <summary>
    /// The apps among <paramref name="integrations"/> that the request seems to be about, most likely first. Only these are connected to.
    /// </summary>
    IReadOnlyList<InstalledIntegration> SelectIntegrations(string request, IReadOnlyList<InstalledIntegration> integrations);

    /// <summary>The tools among <paramref name="tools"/> of <paramref name="integration"/> that the request seems to be about, most likely first, at most <paramref name="max"/>.</summary>
    IReadOnlyList<McpTool> SelectTools(string request, InstalledIntegration integration, IReadOnlyList<McpTool> tools, int max);
}
