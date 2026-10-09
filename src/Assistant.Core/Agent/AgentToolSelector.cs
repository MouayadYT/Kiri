using Assistant.Core.Contracts;
using Assistant.Core.Domain;

namespace Assistant.Core.Agent;

/// <summary>
/// How much of the tool catalog one run may put in front of the model (PROJECT_SPEC §4.8, step 114): a small local model with a 4096-token window
/// does worse the more schemas it is given, and every schema is paid for out of the same window as the conversation.
/// </summary>
/// <param name="MaxTools">The most tools offered in a round.</param>
/// <param name="MaxSchemaCharacters">The most characters of names, descriptions and input schemas offered in a round, taken together.</param>
public sealed record AgentToolBudget(int MaxTools = 16, int MaxSchemaCharacters = 8000)
{
    /// <summary>The budget a chat turn runs under.</summary>
    public static AgentToolBudget Default { get; } = new();

    /// <summary>The characters a tool's definition costs in the prompt.</summary>
    public static int Cost(ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        return tool.Name.Length + tool.Description.Length + tool.InputSchemaJson.Length;
    }
}

/// <summary>
/// Narrows the tools worth offering for a request to the ones the active task is likely to use (PROJECT_SPEC §4.8, step 114). It works on what the
/// registry already chose for the request (the built-in tools that apply to the conversation, and the few tools of the connected apps the request
/// seems to be about), so it never adds a tool; it only leaves some out, to stay within an <see cref="AgentToolBudget"/>.
/// </summary>
public interface IAgentToolSelector
{
    /// <summary>
    /// The tools of <paramref name="candidates"/> to offer for the request in <paramref name="context"/>, in the order they came in. Without
    /// anything to leave out, all of them.
    /// </summary>
    IReadOnlyList<ToolDefinition> Select(ToolContext context, IReadOnlyList<ToolDefinition> candidates);

    /// <summary>
    /// As <see cref="Select(ToolContext, IReadOnlyList{ToolDefinition})"/>, knowing which of <paramref name="candidates"/> are offered because the request
    /// is about them (<see cref="IToolRegistry.FocusedFor"/>): those are the last to be left out. Without an override, the same as the other.
    /// </summary>
    IReadOnlyList<ToolDefinition> Select(ToolContext context, IReadOnlyList<ToolDefinition> candidates, IReadOnlySet<string> focused) => Select(context, candidates);
}

/// <summary>
/// The selector of a run: everything, while it fits the budget; otherwise first the tools that are offered because the request is about them (the
/// clock's for a timer, the messaging tools while someone is being messaged, the connected app the request names), then the tools whose names and
/// descriptions share the most words with the user's request, the earlier registered first among equals, until the budget is spent. Fixed rules and no
/// model, so the same request gets the same tools. A tool that is left out cannot be called in that round: the runner refuses a call to a tool it did
/// not offer.
/// </summary>
/// <remarks>
/// The tools that are always there come first in the registry. Ranked by shared words alone, a reply of one short word ("yes", "hi") shares none, and
/// the cut fell in registration order: in 0.1.142 the messaging conversation's send_message, and a connected app's tools, were the ones left out.
/// </remarks>
/// <param name="budget">What may be offered, or <see langword="null"/> for <see cref="AgentToolBudget.Default"/>.</param>
public sealed class BudgetedToolSelector(AgentToolBudget? budget = null) : IAgentToolSelector
{
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "from", "that", "this", "what", "when", "where", "which", "who", "how", "you", "your", "can", "could",
        "would", "should", "are", "was", "were", "have", "has", "had", "not", "but", "its", "any", "all", "one", "out", "get", "got",
        "please", "tool", "user", "use", "uses", "used", "into", "about", "then", "them", "they", "there", "here", "also", "only", "just",
    };

    private readonly AgentToolBudget _budget = budget ?? AgentToolBudget.Default;

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> Select(ToolContext context, IReadOnlyList<ToolDefinition> candidates) =>
        Select(context, candidates, new HashSet<string>(StringComparer.Ordinal));

    /// <inheritdoc/>
    public IReadOnlyList<ToolDefinition> Select(ToolContext context, IReadOnlyList<ToolDefinition> candidates, IReadOnlySet<string> focused)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(focused);
        if (candidates.Count <= _budget.MaxTools && candidates.Sum(AgentToolBudget.Cost) <= _budget.MaxSchemaCharacters)
        {
            return candidates;
        }

        var wanted = Words(context.Request);
        var ranked = candidates
            .Select((tool, index) => (Tool: tool, Index: index, Score: Score(tool, wanted)))
            .OrderByDescending(entry => focused.Contains(entry.Tool.Name))
            .ThenByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Index);

        var kept = new HashSet<int>();
        var characters = 0;
        foreach (var entry in ranked)
        {
            var cost = AgentToolBudget.Cost(entry.Tool);
            if (kept.Count >= _budget.MaxTools)
            {
                break;
            }

            // One that is too big for what is left is skipped: a smaller one may still fit.
            if (characters + cost > _budget.MaxSchemaCharacters)
            {
                continue;
            }

            kept.Add(entry.Index);
            characters += cost;
        }

        return [.. candidates.Where((_, index) => kept.Contains(index))];
    }

    // A name word is worth three of a description's: the name is what a request is most likely to share ("volume", "calendar", an app's name).
    private static int Score(ToolDefinition tool, HashSet<string> wanted)
    {
        if (wanted.Count == 0)
        {
            return 0;
        }

        var name = Words(tool.Name.Replace('_', ' '));
        var description = Words(tool.Description);
        var score = 0;
        foreach (var word in wanted)
        {
            if (name.Any(candidate => Matches(word, candidate)))
            {
                score += 3;
            }
            else if (description.Any(candidate => Matches(word, candidate)))
            {
                score++;
            }
        }

        return score;
    }

    // The same word, or one that is the other with an ending ("calendars", "calendar"), from four letters.
    private static bool Matches(string left, string right) =>
        left == right
        || (Math.Min(left.Length, right.Length) >= 4
            && (left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal)));

    private static HashSet<string> Words(string? text)
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return words;
        }

        var start = -1;
        for (var index = 0; index <= text.Length; index++)
        {
            var inWord = index < text.Length && char.IsLetterOrDigit(text[index]);
            if (inWord && start < 0)
            {
                start = index;
            }
            else if (!inWord && start >= 0)
            {
                var word = text[start..index].ToLowerInvariant();
                if (word.Length >= 3 && !Stopwords.Contains(word))
                {
                    words.Add(word);
                }

                start = -1;
            }
        }

        return words;
    }
}
