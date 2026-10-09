using System.Security.Cryptography;
using System.Text;
using Assistant.Core.Domain;
using Assistant.Core.Settings;

namespace Assistant.Core.Confirmation;

/// <summary>
/// "Always allow" (PROJECT_SPEC §4.8, step 115): a yes the user chose to give once for an action and every time after, so that a timer, the volume or
/// an application is not asked about again and again. Fixed rules say which questions offer it, and what is kept is one action as it was when the user
/// said so, never a kind of thing or a tool whatever it becomes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Never for a message.</b> A message to a person cannot be taken back, so each one is asked about: the Assistant's own <c>send_message</c>
/// (<see cref="ConfirmationKind.SendMessage"/>) and every tool of a connected app that is about messages or email (it carries the Messaging permission,
/// by what its name says and not by what the app says of it). Nor for a change to files, content sent off the PC, a picture of the screen, access to
/// something of the user's, or a call nothing describes: only for a setting of the PC changed, something opened, and a change in a connected app or a
/// device of the home that is not a message.
/// </para>
/// <para>
/// <b>One action, as it was.</b> What is kept is the tool's name and a fingerprint of its name, description and arguments' schema. A connected app
/// that changes what a tool does, or takes, under the same name is asked about again; so is one of the Assistant's own after an update that changed it.
/// </para>
/// </remarks>
public static class StandingApprovals
{
    /// <summary>The most actions that can be kept as always allowed.</summary>
    public const int MaxKept = 200;

    private const int FingerprintLength = 16;

    /// <summary>Whether a yes to <paramref name="confirmation"/>, asked for a call of <paramref name="tool"/>, may be kept for every later call.</summary>
    public static bool MayBeKept(ToolDefinition tool, ToolConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(confirmation);
        return tool.RiskLevel == RiskLevel.SideEffect
            && tool.RequiredPermission != PermissionCapability.Messaging
            && confirmation.Kind is ConfirmationKind.ChangeSystem or ConfirmationKind.Launch or ConfirmationKind.ConnectedApp
            && IsWellFormed(KeyOf(tool));
    }

    /// <summary>What is kept for <paramref name="tool"/>: its name and the fingerprint of what it is now.</summary>
    public static string KeyOf(ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(tool);
        var described = Encoding.UTF8.GetBytes(tool.Name + "\0" + tool.Description + "\0" + tool.InputSchemaJson);
        return tool.Name + ":" + Convert.ToHexString(SHA256.HashData(described))[..FingerprintLength].ToLowerInvariant();
    }

    /// <summary>The name of the tool an entry is for.</summary>
    public static string ToolOf(string entry) => entry[..Math.Max(0, entry.LastIndexOf(':'))];

    /// <summary>Whether <paramref name="entry"/> is something this class could have kept: a tool's name, a colon, and a fingerprint.</summary>
    public static bool IsWellFormed(string? entry)
    {
        if (entry is null || entry.Length > 160)
        {
            return false;
        }

        var colon = entry.LastIndexOf(':');
        return colon > 0 && entry.Length - colon - 1 == FingerprintLength
            && entry.AsSpan(0, colon).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-.") < 0
            && entry.AsSpan(colon + 1).IndexOfAnyExcept("0123456789abcdef") < 0;
    }

    /// <summary>Whether the user said <paramref name="tool"/>, as it is now, is always allowed.</summary>
    public static bool IsKept(PermissionSettings permissions, ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions.AlwaysAllowed is { Count: > 0 } kept && kept.Contains(KeyOf(tool), StringComparer.Ordinal);
    }

    /// <summary>
    /// <paramref name="permissions"/> with <paramref name="tool"/> always allowed: what was kept for an earlier form of the same tool is replaced, and
    /// with the list full the oldest entry makes room.
    /// </summary>
    public static PermissionSettings Keep(PermissionSettings permissions, ToolDefinition tool)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        var key = KeyOf(tool);
        var kept = (permissions.AlwaysAllowed ?? []).Where(entry => !string.Equals(ToolOf(entry), tool.Name, StringComparison.Ordinal)).ToList();
        kept.Add(key);
        return permissions with { AlwaysAllowed = [.. kept.Skip(Math.Max(0, kept.Count - MaxKept))] };
    }

    /// <summary><paramref name="permissions"/> with the tool called <paramref name="toolName"/> asked about again.</summary>
    public static PermissionSettings Forget(PermissionSettings permissions, string toolName)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        return permissions with
        {
            AlwaysAllowed = [.. (permissions.AlwaysAllowed ?? []).Where(entry => !string.Equals(ToolOf(entry), toolName, StringComparison.Ordinal))],
        };
    }
}
