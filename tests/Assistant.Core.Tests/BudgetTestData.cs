using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;

namespace Assistant.Core.Tests;

/// <summary>
/// Conversations and texts of known size for the budgeting tests. A text made by <see cref="Tokens"/> is exactly as
/// many estimated tokens as words, so a test can say how much room something takes and what is left.
/// </summary>
internal static class BudgetTestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A model whose own window is far above any limit a test sets, so the limits decide.</summary>
    public static readonly ModelInfo LargeModel = new("large-model", 1_000_000);

    private static readonly HeuristicTokenEstimator Estimator = new();

    /// <summary>What the heuristic estimator says <paramref name="text"/> takes.</summary>
    public static int Estimate(string text) => Estimator.Estimate(text);

    /// <summary><paramref name="count"/> words: exactly that many estimated tokens.</summary>
    public static string Tokens(int count) => string.Join(' ', Enumerable.Repeat("word", count));

    /// <summary>A text that starts with <paramref name="tag"/> and takes exactly <paramref name="tokens"/> tokens.</summary>
    public static string Sized(string tag, int tokens)
    {
        var used = Estimate(tag);
        if (used > tokens)
        {
            throw new ArgumentException("The tag is longer than the size asked for.", nameof(tag));
        }

        return used == tokens ? tag : tag + " " + Tokens(tokens - used);
    }

    /// <summary><paramref name="count"/> paragraphs of <paramref name="tokensEach"/> tokens, a paragraph break between.</summary>
    public static string Paragraphs(int count, int tokensEach) =>
        string.Join("\n\n", Enumerable.Repeat(Tokens(tokensEach), count));

    public static Message User(string text, params ContextItem[] context) =>
        new(Guid.NewGuid(), MessageRole.User, text, Now) { ContextItems = context };

    public static Message Answer(string text) => new(Guid.NewGuid(), MessageRole.Assistant, text, Now);

    public static ContextItem File(string name, string? text) =>
        new(Guid.NewGuid(), ContextItemType.File, name) { Text = text };

    public static ContextItem Selection(string name, string text) =>
        new(Guid.NewGuid(), ContextItemType.Selection, name) { Text = text };

    public static ContextItem Retrieved(string name, string text) =>
        new(Guid.NewGuid(), ContextItemType.SearchResults, name) { Text = text };

    public static ContextItem Screenshot(string name, string? text, byte[]? image = null) =>
        new(Guid.NewGuid(), ContextItemType.Screenshot, name) { Text = text, ImageData = image ?? [] };

    /// <summary>
    /// Limits that give the prompt exactly <paramref name="promptTokens"/> of the window, in either mode. The answer's
    /// share is at most half the window, so it is at most the prompt's.
    /// </summary>
    public static ContextLimitSettings Room(int promptTokens, int reserved = 100)
    {
        reserved = Math.Min(reserved, promptTokens);
        return new()
        {
            NormalContextTokens = promptTokens + reserved,
            HeavyContextTokens = promptTokens + reserved,
            ReservedOutputTokens = reserved,
        };
    }
}
