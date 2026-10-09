using Assistant.Core.Domain;
using Assistant.Tools.Integrations;

namespace Assistant.Tools.Mcp;

/// <summary>
/// How much a connected app's tool can change, decided by the Assistant and never by the app (PROJECT_SPEC §4.8, P8 and P9, step 104). A server's
/// own account of its tools (<c>readOnlyHint</c>, <c>destructiveHint</c>) is a hint, and the protocol itself says a client must not trust it unless it
/// trusts the server. So: a tool is <see cref="RiskLevel.SideEffect"/>, which the user confirms each time, unless the user vetted it as read-only
/// (<see cref="IntegrationPermissions.ReadOnlyTools"/>) or trusted the server's hints (<see cref="IntegrationPermissions.TrustToolAnnotations"/>) and the server
/// says it only reads. A tool that says it may destroy data is never offered (there is no destructive tool in this version), whatever else is
/// said, and neither is one the user blocked or, when side effects are not allowed, one that is not known to be read-only.
/// </summary>
internal static class McpToolPolicy
{
    /// <summary>The risk of calling <paramref name="tool"/> of an app with <paramref name="permissions"/>; <see langword="null"/> when it is not to be offered.</summary>
    public static RiskLevel? RiskOf(IntegrationPermissions permissions, McpToolDescriptor tool)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        ArgumentNullException.ThrowIfNull(tool);
        if (permissions.BlockedTools.Contains(tool.Name, StringComparer.Ordinal) || tool.Annotations.DestructiveHint == true)
        {
            return null;
        }

        // The user's own list is their decision about that tool. The server's word is only a hint, even when the user chose to trust hints: a tool that claims to only
        // read while its name says it changes something (create_task, send_message, delete_note) is not believed, so a server cannot have its writes run without a question.
        var readOnly = permissions.ReadOnlyTools.Contains(tool.Name, StringComparer.Ordinal)
            || permissions.TrustToolAnnotations && tool.Annotations.ReadOnlyHint == true && !NameSaysItChanges(tool.Name);
        if (readOnly)
        {
            // Reading can be turned off for an app (step 119): none of its tools that only read is then offered.
            return permissions.AllowReads ? RiskLevel.ReadOnly : null;
        }

        return permissions.AllowSideEffects ? RiskLevel.SideEffect : null;
    }

    // The words that a tool's name has when it changes something somewhere: a task created, a message sent, a file moved, an account changed.
    private static readonly HashSet<string> ChangingWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "create", "add", "insert", "append", "delete", "remove", "drop", "destroy", "erase", "clear", "purge", "trash", "update", "edit", "modify", "change",
        "set", "write", "save", "put", "patch", "replace", "rename", "move", "copy", "upload", "send", "post", "reply", "forward", "publish", "share", "invite", "assign",
        "complete", "close", "reopen", "archive", "cancel", "schedule", "book", "pay", "buy", "order", "transfer", "grant", "revoke", "enable", "disable", "install",
        "uninstall", "execute", "run", "restart", "reset", "sync", "import", "export", "merge", "commit", "push", "approve", "reject", "submit",
    };

    /// <summary>Whether the words of a tool's name (split at underscores, hyphens, dots and capital letters) include one that says it changes something.</summary>
    internal static bool NameSaysItChanges(string name)
    {
        var word = new System.Text.StringBuilder();
        for (var index = 0; index <= name.Length; index++)
        {
            var character = index < name.Length ? name[index] : '_';
            var boundary = !char.IsLetterOrDigit(character) || index > 0 && char.IsUpper(character) && char.IsLower(name[index - 1]);
            if (boundary && word.Length > 0)
            {
                if (ChangingWords.Contains(word.ToString()))
                {
                    return true;
                }

                word.Clear();
            }

            if (char.IsLetterOrDigit(character))
            {
                word.Append(character);
            }
        }

        return false;
    }
}
