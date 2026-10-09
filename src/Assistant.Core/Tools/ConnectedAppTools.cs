namespace Assistant.Core.Tools;

/// <summary>
/// What marks a tool as one of a connected app's (PROJECT_SPEC §4.8, step 104): its name starts with <see cref="Prefix"/>. The name of a
/// built-in tool never does, so the two never collide and a tool's origin can be told from its name alone.
/// </summary>
public static class ConnectedAppTools
{
    /// <summary>The start of the name of every tool that comes from a connected app.</summary>
    public const string Prefix = "mcp_";

    /// <summary>Whether <paramref name="toolName"/> names a tool that comes from a connected app.</summary>
    public static bool IsConnectedAppTool(string? toolName) =>
        toolName is not null && toolName.StartsWith(Prefix, StringComparison.Ordinal);
}
