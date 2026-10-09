namespace Assistant.Core.Domain;

/// <summary>Why a capability may or may not be used.</summary>
public enum PermissionDecisionReason
{
    /// <summary>The Assistant can do it and the user allows it (or has just said yes to this use).</summary>
    Granted = 0,

    /// <summary>The Assistant can do it and the user has turned it off.</summary>
    TurnedOff = 1,

    /// <summary>
    /// This build cannot do it, or refuses to (<see cref="PermissionAvailability"/>): it is off whatever the settings say.
    /// </summary>
    NotAvailable = 2,

    /// <summary>
    /// The user wants to be asked each time (<see cref="PermissionMode.AskEveryTime"/>) and nobody has asked about this use yet. It is not allowed: code that can
    /// ask (<c>IPermissionGate</c>) asks, and code that cannot treats it as a no.
    /// </summary>
    AskEveryTime = 3,

    /// <summary>The user was asked about this use and said no.</summary>
    Declined = 4,

    /// <summary>The user was to be asked about this use, but did not answer in time or nothing could show the question: it is not allowed.</summary>
    CouldNotAsk = 5,
}

/// <summary>
/// The answer to whether a capability may be used now. It holds nothing but the capability and the reason, so it is safe to
/// log.
/// </summary>
/// <param name="Capability">The capability asked about.</param>
/// <param name="Reason">Why it may or may not be used.</param>
public sealed record PermissionDecision(PermissionCapability Capability, PermissionDecisionReason Reason)
{
    /// <summary>Whether the capability may be used.</summary>
    public bool IsAllowed => Reason == PermissionDecisionReason.Granted;

    /// <summary>Whether the capability would be allowed once the user says yes to this use: it is set to ask every time and has not been asked about.</summary>
    public bool NeedsAsking => Reason == PermissionDecisionReason.AskEveryTime;

    /// <summary>
    /// Whether a feature that uses the capability is worth offering: it is allowed now, or it is set to ask each time and the user will be asked when it is used. It decides what
    /// is shown to the user or the model, never whether a use goes ahead (that is <see cref="IsAllowed"/>, after the question).
    /// </summary>
    public bool CouldBeAllowed => IsAllowed || NeedsAsking;
}
