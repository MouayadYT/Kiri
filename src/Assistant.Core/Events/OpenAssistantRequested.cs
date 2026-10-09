namespace Assistant.Core.Events;

/// <summary>The user asked to open the Search or Ask bar.</summary>
/// <param name="Source">How the user asked.</param>
public sealed record OpenAssistantRequested(InvocationSource Source);
