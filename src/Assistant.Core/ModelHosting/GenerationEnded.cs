namespace Assistant.Core.ModelHosting;

/// <summary>Ends the answer to a <see cref="GenerationRequest"/>; no more replies follow for it.</summary>
/// <param name="Reason">Why the generation stopped.</param>
public sealed record GenerationEnded(GenerationStopReason Reason) : ModelHostReply
{
    /// <summary>Tokens in the prompt, when the engine reports them.</summary>
    public int? PromptTokens { get; init; }

    /// <summary>Tokens generated, when the engine reports them.</summary>
    public int? OutputTokens { get; init; }

    internal override bool IsWellFormed() =>
        Enum.IsDefined(Reason) && PromptTokens is null or >= 0 && OutputTokens is null or >= 0;
}
