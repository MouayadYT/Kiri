using System.Text;
using Assistant.Core.Budgeting;
using Assistant.Core.Domain;

namespace Assistant.Core.Context;

/// <summary>One item of a conversation's context, with where it came from and how it fared the last time a prompt was fitted.</summary>
/// <param name="Item">
/// The item. While it waits for a question it is whole; once it has been sent only its descriptor is kept (no text, no
/// image), because the conversation's message carries the content.
/// </param>
/// <param name="Priority">How it ranks now: as context for the next question, or as context of an earlier one.</param>
/// <param name="Provenance">Every time it was supplied, oldest first. More than one when duplicates were merged.</param>
/// <param name="IsSent">Whether it was sent with an earlier question, rather than waiting for the next.</param>
/// <param name="LastFit">What the last prompt fitted did to it, or <see langword="null"/> when none has been fitted with it.</param>
public sealed record ContextEntry(
    ContextItem Item,
    ContextPriority Priority,
    IReadOnlyList<ContextProvenance> Provenance,
    bool IsSent,
    ContextFate? LastFit)
{
    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Type = {Item.Type}, Priority = {Priority}, IsSent = {IsSent}, LastFit = {LastFit}");
        return true;
    }
}
