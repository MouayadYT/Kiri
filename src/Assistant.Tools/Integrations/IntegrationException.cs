namespace Assistant.Tools.Integrations;

/// <summary>Why a change to the installed integrations was refused.</summary>
public enum IntegrationFailure
{
    /// <summary>The integration breaks a rule (<see cref="IntegrationRules"/>).</summary>
    Invalid = 0,

    /// <summary>An integration with that id is installed already.</summary>
    Duplicate = 1,

    /// <summary>No integration has that id.</summary>
    NotFound = 2,

    /// <summary>The list could not be read or written.</summary>
    StoreFailed = 3,
}

/// <summary>
/// A change to the installed integrations was refused. The message and <see cref="Problems"/> say what rule was broken without repeating any
/// value (an address, a path, a secret's name), so both are safe to show and to log.
/// </summary>
public sealed class IntegrationException : Exception
{
    /// <summary>Creates the exception.</summary>
    public IntegrationException(IntegrationFailure failure, IReadOnlyList<string>? problems = null, Exception? inner = null)
        : base(Describe(failure), inner)
    {
        Failure = failure;
        Problems = problems ?? [];
    }

    /// <summary>What kind of refusal it was.</summary>
    public IntegrationFailure Failure { get; }

    /// <summary>The rules that were broken, for <see cref="IntegrationFailure.Invalid"/>.</summary>
    public IReadOnlyList<string> Problems { get; }

    private static string Describe(IntegrationFailure failure) => failure switch
    {
        IntegrationFailure.Invalid => "The integration breaks a rule.",
        IntegrationFailure.Duplicate => "An integration with that id is installed already.",
        IntegrationFailure.NotFound => "No integration has that id.",
        _ => "The installed integrations could not be read or written.",
    };
}
