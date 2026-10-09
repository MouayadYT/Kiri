namespace Assistant.Core.Budgeting;

/// <summary>What fitting the prompt into the model's window did to one piece of context. Holds no content.</summary>
/// <param name="ItemId">The <see cref="Domain.ContextItem.Id"/> of the item.</param>
/// <param name="Priority">How the item ranked.</param>
/// <param name="Fate">What became of it.</param>
public readonly record struct ContextItemFit(Guid ItemId, ContextPriority Priority, ContextFate Fate);
