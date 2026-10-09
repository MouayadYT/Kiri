namespace Assistant.Core.ModelHosting;

/// <summary>
/// The final reply to a request that failed. It carries only a code, never text, so it cannot hold private content.
/// </summary>
/// <param name="Code">What went wrong.</param>
public sealed record ModelHostError(ModelHostErrorCode Code) : ModelHostReply
{
    internal override bool IsWellFormed() => Enum.IsDefined(Code);
}
