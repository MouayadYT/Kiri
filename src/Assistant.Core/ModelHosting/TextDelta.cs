using System.Text;

namespace Assistant.Core.ModelHosting;

/// <summary>Streams part of a <see cref="GenerationRequest"/>'s answer: text to append to it.</summary>
/// <param name="Text">The generated text, which is private content (PROJECT_SPEC §3.2).</param>
public sealed record TextDelta(string Text) : ModelHostReply
{
    internal override bool IsWellFormed() => Text is not null;

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    protected override bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Length = {Text?.Length}");
        return true;
    }
}
