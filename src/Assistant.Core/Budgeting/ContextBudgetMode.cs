namespace Assistant.Core.Budgeting;

/// <summary>Which of the two context limits a request is held to (<see cref="Settings.ContextLimitSettings"/>).</summary>
public enum ContextBudgetMode
{
    /// <summary>An ordinary conversation, with nothing attached: held to the smaller limit, so chat stays quick.</summary>
    Normal = 0,

    /// <summary>
    /// A conversation that carries attached or retrieved context, such as a file, a page or search results: given the
    /// larger limit, which documents need.
    /// </summary>
    Heavy = 1,
}
