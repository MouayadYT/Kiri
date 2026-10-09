using Assistant.Core.Domain;

namespace Assistant.Core.Context;

/// <summary>Where and when a piece of context was supplied: one per supply, so an item supplied twice has two.</summary>
/// <param name="Source">How the context came in.</param>
/// <param name="Origin">
/// A short, fixed label for the part of the Assistant that supplied it, such as <c>composer</c> or <c>explorer</c>. It
/// never holds anything the user wrote or a path, so it is safe to log.
/// </param>
/// <param name="AddedAt">When it was supplied.</param>
public readonly record struct ContextProvenance(ContextSource Source, string Origin, DateTimeOffset AddedAt);
