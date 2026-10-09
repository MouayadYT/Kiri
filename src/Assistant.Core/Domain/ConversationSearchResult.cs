namespace Assistant.Core.Domain;

/// <summary>A conversation that matched a search, with a snippet of what matched when a message did.</summary>
/// <param name="Conversation">The conversation, as a history list shows it.</param>
/// <param name="Snippet">
/// The words around the match in the conversation's most recent message that has one, or <see langword="null"/> when
/// only the conversation's title matched.
/// </param>
public sealed record ConversationSearchResult(ConversationSummary Conversation, SearchSnippet? Snippet);
