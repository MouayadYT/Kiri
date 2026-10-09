using Assistant.Core.Domain;

namespace Assistant.Core.Permissions;

/// <summary>The words that say why a capability may not be used, one place for them so that every surface says the same thing (PROJECT_SPEC §4.9, step 119).</summary>
public static class PermissionTexts
{
    /// <summary>Why <paramref name="decision"/> does not allow the use, in a sentence for the user. It is meant for a decision that is not allowed.</summary>
    public static string WhyNot(PermissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var title = PermissionCatalog.Get(decision.Capability).Title;
        return decision.Reason switch
        {
            PermissionDecisionReason.TurnedOff => $"{title} is turned off in Settings, under Permissions.",
            PermissionDecisionReason.NotAvailable => $"{title} is not available in this version.",
            PermissionDecisionReason.AskEveryTime => $"{title} is set to ask every time, and this use was not asked about.",
            PermissionDecisionReason.Declined => $"You did not allow {title} this time.",
            PermissionDecisionReason.CouldNotAsk => $"{title} is set to ask every time, and the question was not answered.",
            _ => $"{title} is allowed.",
        };
    }

    /// <summary>The same for the model, which passes it on to the user: it says nothing was done and to tell the user.</summary>
    public static string ForModel(PermissionDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        var title = PermissionCatalog.Get(decision.Capability).Title;
        return decision.Reason switch
        {
            PermissionDecisionReason.TurnedOff => $"{title} is turned off in Settings, under Permissions, so that tool cannot run. Tell the user.",
            PermissionDecisionReason.NotAvailable => $"{title} is not available in this version, so that tool cannot run. Tell the user.",
            PermissionDecisionReason.Declined =>
                $"The user did not allow {title} this time, so nothing was done. Do not try it again unless the user asks you to.",
            PermissionDecisionReason.CouldNotAsk =>
                $"{title} is set to ask the user each time, and the user did not answer, so nothing was done. Tell the user, and try again only if they want it.",
            _ => $"{title} is set to ask every time and cannot be used here, so that tool cannot run. Tell the user.",
        };
    }
}
