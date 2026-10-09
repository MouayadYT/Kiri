using Assistant.Core.Domain;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// Which permission (Settings, Permissions) a tool of a connected app needs, decided by the Assistant and never by the app (PROJECT_SPEC §4.9, step 119). The user can have set one for the
/// whole integration (<see cref="IntegrationPermissions.RequiredCapability"/>), which every tool of it needs. Besides, a tool that is about a calendar's events needs the Calendar permission
/// and one that is about messages or email needs the Messaging permission, whatever app it comes from: they are told apart by the words of the tool's <em>name and title</em> (the
/// description is the app's own marketing and may mention anything), so a calendar or a messaging app the user connected cannot be read or used while the permission that covers it is off,
/// and is asked about each time when it is set to ask. Nothing a server says about itself can lower what a tool needs.
/// </summary>
internal static class McpToolPermission
{
    // The words of a tool's name that say it is about a calendar's events, and about messages. Whole words (a name in camel or snake case is taken apart), singular.
    private static readonly string[] CalendarWords = ["calendar", "event", "meeting", "appointment", "agenda"];

    private static readonly string[] MessagingWords = ["message", "email", "mail", "dm", "chat", "sms", "whatsapp", "telegram", "imessage", "inbox"];

    /// <summary>The permission every call of <paramref name="tool"/>, a tool of <paramref name="integration"/>, needs; <see langword="null"/> when it needs none.</summary>
    public static PermissionCapability? For(InstalledIntegration integration, McpToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(integration);
        ArgumentNullException.ThrowIfNull(tool);
        return integration.Permissions.RequiredCapability ?? Infer(tool.Name, tool.Title);
    }

    /// <summary>The permission a tool needs by the words of its <paramref name="name"/> and <paramref name="title"/> alone; <see langword="null"/> when they are about neither.</summary>
    public static PermissionCapability? Infer(string name, string? title)
    {
        var words = CapabilityMatcher.Stems(name);
        words.UnionWith(CapabilityMatcher.Stems(title));

        // A tool about messages is the more sensitive of the two (it can reach other people), so it wins when a name has both ("send_event_message").
        if (MessagingWords.Any(words.Contains))
        {
            return PermissionCapability.Messaging;
        }

        return CalendarWords.Any(words.Contains) ? PermissionCapability.Calendar : null;
    }
}
