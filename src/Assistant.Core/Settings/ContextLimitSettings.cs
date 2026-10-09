using Assistant.Core.Contracts;

namespace Assistant.Core.Settings;

/// <summary>How much context one request may include.</summary>
public sealed record ContextLimitSettings
{
    /// <summary>
    /// Defaults for the downloadable models: 8,000 tokens for ordinary chats and 32,000 while a conversation carries files, with 1K kept for the answer.
    /// </summary>
    public static readonly ContextLimitSettings Roomy = new();

    /// <summary>Most files attached to one request (PROJECT_SPEC §4.4).</summary>
    public int MaxAttachedFiles { get; init; } = 10;

    /// <summary>Most files retrieved from Windows Search to ground one answer (PROJECT_SPEC §4.7).</summary>
    public int MaxRetrievedFiles { get; init; } = 5;

    /// <summary>Largest file, in bytes, whose text is extracted.</summary>
    public long MaxFileSizeBytes { get; init; } = 50L * 1024 * 1024;

    /// <summary>
    /// Most tokens of the model's context window (prompt and answer together) an ordinary conversation may use, which
    /// keeps chat quick. It is also the window the model is loaded with, unless the user set one (<c>ModelSettings.ContextLength</c>).
    /// Zero or less leaves it to the model's window.
    /// </summary>
    public int NormalContextTokens { get; init; } = ModelFiles.DefaultContextLength;

    /// <summary>
    /// Most tokens of the model's context window (prompt and answer together) a conversation that carries attached or
    /// retrieved context may use, which is slower to read but is what documents need. While a conversation carries files the model
    /// is loaded with this window (<c>ContextWindowPlan</c>), and with the ordinary one again afterwards. Zero or less leaves it to
    /// the model's window.
    /// </summary>
    public int HeavyContextTokens { get; init; } = ModelFiles.DocumentContextLength;

    /// <summary>
    /// Tokens of the context window reserved for the answer (PROJECT_SPEC §5.5), which is also the most the model is
    /// asked to write. It never takes more than half the window. Zero or less means the default.
    /// </summary>
    public int ReservedOutputTokens { get; init; } = 1024;
}
