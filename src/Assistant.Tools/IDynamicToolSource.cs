using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Tools;

/// <summary>
/// A source of tools that are not fixed in code but loaded by what the user asks (PROJECT_SPEC §4.8, step 104): the tools of the apps the user has
/// connected. The tool registry and the executor take them in beside the built-in tools and apply exactly the same rules to them. A source never
/// adds a tool to the registry for good: it says which tools are worth offering for one request, and only those can be called in that conversation.
/// </summary>
public interface IDynamicToolSource
{
    /// <summary>
    /// Gets the tools worth offering for the request in <paramref name="context"/> ready, which can mean starting or reaching an app. It decides the
    /// tools <see cref="Offered"/> and <see cref="Find"/> give for that conversation and request, and replaces what it offered the conversation before. It
    /// never throws except when <paramref name="cancellationToken"/> is cancelled: what cannot be loaded, or is too slow, is left out.
    /// </summary>
    Task PrepareAsync(ToolContext context, CancellationToken cancellationToken);

    /// <summary>The tools that <see cref="PrepareAsync"/> chose for the request and conversation in <paramref name="context"/>; none for any other request.</summary>
    IReadOnlyList<ITool> Offered(ToolContext context);

    /// <summary>
    /// The tool called <paramref name="name"/> if it was offered to the conversation in <paramref name="context"/> for its request, so that a model can
    /// call only what it was given; <see langword="null"/> otherwise.
    /// </summary>
    ITool? Find(ToolContext context, string name);

    /// <summary>The definition of a tool this source has loaded and knows by <paramref name="name"/>, offered or not, or <see langword="null"/>. For naming a tool in a log or in a message about it.</summary>
    ToolDefinition? Describe(string name);
}
