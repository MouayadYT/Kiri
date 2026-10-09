namespace Assistant.Core.Confirmation;

/// <summary>How the question "may I do this?" ended (PROJECT_SPEC §4.8, step 115). Only <see cref="Approved"/> lets a call run.</summary>
public enum ConfirmationDecision
{
    /// <summary>The user said yes, to this call and no other.</summary>
    Approved = 0,

    /// <summary>The user said no.</summary>
    Declined = 1,

    /// <summary>The user did not answer in time, so the question was withdrawn. Nothing was done.</summary>
    NoAnswer = 2,

    /// <summary>Nothing could show the question to the user (no window holds the conversation), so it was not asked. Nothing was done.</summary>
    CouldNotAsk = 3,
}
