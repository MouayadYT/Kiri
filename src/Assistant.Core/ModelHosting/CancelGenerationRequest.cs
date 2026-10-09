namespace Assistant.Core.ModelHosting;

/// <summary>
/// Stops a running generation. It has no reply of its own: the generation ends with
/// <see cref="GenerationEnded"/> and <see cref="GenerationStopReason.Cancelled"/>, or had already ended, in which case
/// nothing happens.
/// </summary>
/// <param name="RequestId">The id of the <see cref="GenerationRequest"/> to stop.</param>
public sealed record CancelGenerationRequest(long RequestId) : ModelHostRequest
{
    internal override bool IsWellFormed() => RequestId > 0;
}
