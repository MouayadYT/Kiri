namespace Assistant.Core.Events;

/// <summary>The user picked a saved conversation to open or continue, for example in the History window.</summary>
/// <param name="ConversationId">The conversation's identifier.</param>
public sealed record ConversationSelected(Guid ConversationId);
