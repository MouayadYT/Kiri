using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools;

/// <summary>
/// The app's <see cref="IToolRegistry"/>: the catalog of the tools registered, in the order they were registered. Which tools exist
/// and what each is (its name, arguments, side-effect category, permission and time limit) is fixed in code (PROJECT_SPEC §4.8):
/// nothing the model or a file says can add one or change it. A tool is registered only if it meets <see cref="ToolDefinitionGuard"/>:
/// a stable name, a typed input schema, a time limit, no tool that can destroy data and none that runs a command or a script. The tools of the
/// apps the user has connected (step 104) are not registered here for good: they come from an <see cref="IDynamicToolSource"/>, which loads the few
/// that fit a request, and are held to the same rules; their names all start with <see cref="ConnectedAppTools.Prefix"/>, which no built-in tool's does.
/// </summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ToolDefinition> _byName = new(StringComparer.Ordinal);
    private readonly ITool[] _tools;
    private readonly IDynamicToolSource? _dynamicTools;

    /// <summary>Creates the catalog of <paramref name="tools"/>.</summary>
    /// <exception cref="ArgumentException">
    /// Two tools have the same name, or one is a tool that can destroy data, or one does not meet the rules for a tool
    /// (<see cref="ToolDefinitionGuard"/>), such as one that runs a command, or one has a name that is reserved for a connected app's tool.
    /// </exception>
    public ToolRegistry(IEnumerable<ITool> tools, IDynamicToolSource? dynamicTools = null)
    {
        ArgumentNullException.ThrowIfNull(tools);
        _dynamicTools = dynamicTools;
        _tools = [.. tools];
        Tools = [.. _tools.Select(tool => tool.Definition)];
        foreach (var tool in _tools)
        {
            var definition = tool.Definition;
            if (definition?.RiskLevel == RiskLevel.Destructive)
            {
                throw new ArgumentException("No tool that can destroy data is registered in this version.", nameof(tools));
            }

            if (ToolDefinitionGuard.Problem(definition!, tool.Timeout) is { } problem)
            {
                throw new ArgumentException(problem, nameof(tools));
            }

            if (ConnectedAppTools.IsConnectedAppTool(definition!.Name))
            {
                throw new ArgumentException($"A built-in tool's name does not start with {ConnectedAppTools.Prefix}: that is how a connected app's tool is told.", nameof(tools));
            }

            if (!_byName.TryAdd(definition.Name, definition))
            {
                throw new ArgumentException($"Two tools are named {definition.Name}.", nameof(tools));
            }
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> Tools { get; }

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> ToolsFor(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        // A message that answers a question one of the tools had asked is that tool's alone (ITool.Claims).
        var claimed = _tools.Where(tool => tool.Claims(context) && tool.IsOffered(context)).Select(tool => tool.Definition).ToList();
        if (claimed.Count > 0)
        {
            return claimed;
        }

        var builtIn = _tools.Where(tool => tool.IsOffered(context)).Select(tool => tool.Definition);
        return _dynamicTools is null ? [.. builtIn] : [.. builtIn, .. _dynamicTools.Offered(context).Select(tool => tool.Definition)];
    }

    /// <inheritdoc/>
    /// <remarks>The built-in tools that say so (<see cref="ITool.IsFocused"/>), and every connected app's tool offered: those are loaded only for a request that names the app.</remarks>
    public IReadOnlySet<string> FocusedFor(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var focused = _tools.Where(tool => tool.IsOffered(context) && tool.IsFocused(context)).Select(tool => tool.Definition.Name).ToHashSet(StringComparer.Ordinal);
        if (_dynamicTools is not null)
        {
            focused.UnionWith(_dynamicTools.Offered(context).Select(tool => tool.Definition.Name));
        }

        return focused;
    }

    /// <inheritdoc/>
    public async Task PrepareToolsAsync(ToolContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // The connected apps and the built-in tools that need to find something out are asked side by side. A built-in tool that cannot find out is offered as it was: it is not
        // the turn's failure, and its own checks run when it is called.
        var dynamicTask = _dynamicTools is null ? Task.CompletedTask : _dynamicTools.PrepareAsync(context, cancellationToken);
        var builtIn = _tools.Select(tool => PrepareToolAsync(tool, context, cancellationToken));
        await Task.WhenAll(builtIn.Prepend(dynamicTask)).ConfigureAwait(false);
    }

    private static async Task PrepareToolAsync(ITool tool, ToolContext context, CancellationToken cancellationToken)
    {
        try
        {
            await tool.PrepareAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
        {
            // See PrepareToolsAsync.
        }
    }

    /// <inheritdoc/>
    public ToolDefinition? Find(string name) =>
        name is null ? null : _byName.TryGetValue(name, out var tool) ? tool : _dynamicTools?.Describe(name);
}
