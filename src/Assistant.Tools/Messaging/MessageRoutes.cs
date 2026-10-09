using Assistant.Tools.Messaging.ConnectedApps;

namespace Assistant.Tools.Messaging;

/// <summary>Where a person's messages go, as it is remembered (Settings, under Memory).</summary>
public static class MessageRoutes
{
    /// <summary>
    /// What is kept for a person when the user chooses only the service their messages go through ("iMessage"): no chat, so the chat is found among
    /// the person's by that service each time, as if the user had named it.
    /// </summary>
    public static string ForService(string service)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(service);
        return McpMessagingProvider.SavedRoute.WriteService(service.Trim());
    }
}