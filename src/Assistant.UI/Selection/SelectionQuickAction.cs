namespace Assistant.UI.Selection;

/// <summary>What a quick action of the Ask panel is for.</summary>
public enum QuickActionKind
{
    /// <summary>A shorter version of the selected text.</summary>
    Summarize,

    /// <summary>What the selected text says, in plain terms.</summary>
    Explain,

    /// <summary>The selected text written again, more clearly.</summary>
    Rewrite,

    /// <summary>A problem in the selected text, worked out.</summary>
    Solve,

    /// <summary>What a word or phrase in the selected text means.</summary>
    Define,

    /// <summary>Nothing is prepared: the user's own question about the selected text.</summary>
    AskAnything,
}

/// <summary>
/// One quick action offered with the selected text (PROJECT_SPEC §4.5): a name, and the request it prepares. An action is a prompt
/// template the user picks, not a tool (§4.8). Picking one writes its <see cref="Prompt"/> in the composer for the user to read,
/// change and send; it never sends it, and <see cref="QuickActionKind.AskAnything"/> prepares nothing at all.
/// </summary>
/// <param name="Kind">What the action is for.</param>
/// <param name="Title">What the row says.</param>
/// <param name="Prompt">The request written in the composer, empty for an action that leaves it as it is.</param>
/// <param name="IconKey">The key, in the theme's resources, of the glyph drawn before the title (<c>Themes/Controls/QuickActions.xaml</c>).</param>
public sealed record SelectionQuickAction(QuickActionKind Kind, string Title, string Prompt, string IconKey);

/// <summary>The quick actions offered with selected text, in the order they are listed.</summary>
public static class SelectionQuickActions
{
    /// <summary>The actions, top to bottom: the five that prepare a request, then the free question.</summary>
    public static IReadOnlyList<SelectionQuickAction> All { get; } =
    [
        new(QuickActionKind.Summarize, "Summarize", "Summarize the selected text.", "Glyph.Quick.Summarize"),
        new(QuickActionKind.Explain, "Explain", "Explain the selected text.", "Glyph.Quick.Explain"),
        new(QuickActionKind.Rewrite, "Rewrite", "Rewrite the selected text so it reads more clearly.", "Glyph.Quick.Rewrite"),
        new(QuickActionKind.Solve, "Solve", "Solve the selected problem and show the steps.", "Glyph.Quick.Solve"),
        new(QuickActionKind.Define, "Define", "Define the selected text.", "Glyph.Quick.Define"),
        new(QuickActionKind.AskAnything, "Ask Anything", "", "Glyph.Quick.AskAnything"),
    ];
}
