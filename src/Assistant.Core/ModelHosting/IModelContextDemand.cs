namespace Assistant.Core.ModelHosting;

/// <summary>
/// Tells the model service what the conversation being answered needs of the context window (PROJECT_SPEC §5.5): an ordinary conversation has the
/// ordinary window, and one that carries files the larger one (<c>ContextWindowPlan</c>). Whoever starts a turn says which it is before it asks which
/// model answers; the model is then loaded, or loaded again, with that window the next time it generates. It stays as it was said until it is said again,
/// so everything a turn does (reading a file, taking notes on several) has the same window.
/// </summary>
public interface IModelContextDemand
{
    /// <summary>Whether the conversation being answered carries files.</summary>
    bool Documents { get; }

    /// <summary>Says whether the conversation being answered carries files.</summary>
    /// <returns>Whether that changed what the model is asked for.</returns>
    bool Use(bool documents);
}
