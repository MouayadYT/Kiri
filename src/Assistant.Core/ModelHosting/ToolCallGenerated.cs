using Assistant.Core.Domain;

namespace Assistant.Core.ModelHosting;

/// <summary>
/// Streams part of a <see cref="GenerationRequest"/>'s answer: the model asked to call one of the request's tools.
/// The app validates the call before anything runs (PROJECT_SPEC §4.8).
/// </summary>
/// <param name="Call">The requested call, with its arguments exactly as the model emitted them.</param>
public sealed record ToolCallGenerated(ToolCall Call) : ModelHostReply
{
    internal override bool IsWellFormed() =>
        Call is { Id: not null, ToolName: not null, ArgumentsJson: not null };
}
