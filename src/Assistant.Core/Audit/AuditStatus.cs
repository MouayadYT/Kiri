namespace Assistant.Core.Audit;

/// <summary>Where one step of what the Assistant did stands, or how it ended (PROJECT_SPEC §4.8, step 117).</summary>
public enum AuditStatus
{
    /// <summary>It is being done.</summary>
    Running = 0,

    /// <summary>It is waiting for the user to say whether it may be done.</summary>
    WaitingForYou = 1,

    /// <summary>It was done.</summary>
    Succeeded = 2,

    /// <summary>It was tried and did not work.</summary>
    Failed = 3,

    /// <summary>It was not done because the user did not allow it, did not answer in time, or could not be asked.</summary>
    Declined = 4,

    /// <summary>It took longer than it is allowed and was given up.</summary>
    TimedOut = 5,

    /// <summary>It was stopped before it finished.</summary>
    Cancelled = 6,

    /// <summary>It was not tried: the call could not be read, was a repeat, was not offered, or the run had no room for it.</summary>
    Skipped = 7,

    /// <summary>The app closed while it was going on, so how it ended is not known.</summary>
    Interrupted = 8,
}

/// <summary>What kind of thing an <see cref="AuditEntry"/> records.</summary>
public enum AuditKind
{
    /// <summary>A tool the model called, in a run of the agent loop.</summary>
    ToolCall = 0,

    /// <summary>A look on the web for an integration (the Integration Finder).</summary>
    FindIntegration = 1,

    /// <summary>An integration that passed the review was offered to the user.</summary>
    OfferIntegration = 2,

    /// <summary>The user turned an offer down.</summary>
    DeclineIntegration = 3,

    /// <summary>An integration was installed, after the user approved it.</summary>
    InstallIntegration = 4,

    /// <summary>An integration was replaced by a newer version, after the user approved it.</summary>
    UpdateIntegration = 5,

    /// <summary>The latest version of one integration was looked up, and offered when it is newer.</summary>
    FindIntegrationUpdate = 6,

    /// <summary>The installed integrations were checked for newer versions.</summary>
    CheckIntegrationUpdates = 7,

    /// <summary>An integration was removed.</summary>
    RemoveIntegration = 8,

    /// <summary>An integration was reconnected.</summary>
    ReconnectIntegration = 9,

    /// <summary>An integration was turned on or off.</summary>
    SwitchIntegration = 10,

    /// <summary>What an integration may do was changed in Settings (reading, changing things, network, account, updates).</summary>
    ChangeIntegrationAccess = 11,

    /// <summary>An app whose server is known was connected, and signed in to, after the user approved it.</summary>
    ConnectIntegration = 12,

    /// <summary>The user signed in to a connected app again, or signed out of it.</summary>
    SignInIntegration = 13,
}

/// <summary>How a multi-step run of the agent stands, or how it ended.</summary>
public enum AgentTaskStatus
{
    /// <summary>It is going on.</summary>
    Running = 0,

    /// <summary>The Assistant came to its answer.</summary>
    Completed = 1,

    /// <summary>The Assistant had to stop and answer with what it had: it ran out of steps or time, or was going in circles.</summary>
    Incomplete = 2,

    /// <summary>The user, or the Searching chip, stopped it.</summary>
    Cancelled = 3,

    /// <summary>The model could not go on.</summary>
    Failed = 4,

    /// <summary>The app closed while it was going on.</summary>
    Interrupted = 5,
}

/// <summary>Questions about the statuses.</summary>
public static class AuditStatuses
{
    /// <summary>Whether the step has ended, one way or another.</summary>
    public static bool IsFinished(this AuditStatus status) => status is not (AuditStatus.Running or AuditStatus.WaitingForYou);

    /// <summary>Whether the step ended in something that did not go as the user wanted and that the Assistant should explain.</summary>
    public static bool IsProblem(this AuditStatus status) => status is AuditStatus.Failed or AuditStatus.TimedOut or AuditStatus.Cancelled or AuditStatus.Interrupted;

    /// <summary>Whether the run has ended, one way or another.</summary>
    public static bool IsFinished(this AgentTaskStatus status) => status != AgentTaskStatus.Running;
}
