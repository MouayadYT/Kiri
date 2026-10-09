using System.Text;
using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;

namespace Assistant.Core.Orchestration;

/// <summary>A prompt built for one turn, and what the user must be told about it.</summary>
/// <param name="Request">The request for the model.</param>
/// <param name="Notices">
/// What was changed in the prompt, such as a screenshot sent as its text, for the user to see (PROJECT_SPEC §4.4). Empty
/// when the prompt is as asked. What was left out or cut short for want of room is in <see cref="ContextWarnings"/>.
/// </param>
public sealed record BuiltPrompt(ModelRequest Request, IReadOnlyList<string> Notices)
{
    /// <summary>
    /// What fitting the conversation into the model's context window did, or <see langword="null"/> when it was built
    /// without limits.
    /// </summary>
    public ContextBudgetReport? Budget { get; init; }

    /// <summary>
    /// What each of the request's images shows, in the same order (<see cref="ModelRequest.Images"/>): a screenshot is made ready for the
    /// model differently from a photo, so that its small text stays readable (<see cref="Imaging.ImageContent"/>). Empty when the prompt
    /// carries no image, and every image is a picture when it is shorter than the images.
    /// </summary>
    public IReadOnlyList<Imaging.ImageContent> ImageContents { get; init; } = [];

    /// <summary>
    /// What was left out of the prompt, or cut short, because it did not fit the model's window (earlier messages, the
    /// middle of the user's own message, context items), said in words that hold no content, for the user to be warned
    /// about. Empty when everything fit or the prompt was built without limits.
    /// </summary>
    public IReadOnlyList<string> ContextWarnings { get; init; } = [];

    // Keeps private content (PROJECT_SPEC §3.2) out of ToString, and so out of logs.
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Request = {Request}, Notices = {Notices.Count}, ContextWarnings = {ContextWarnings.Count}");
        return true;
    }
}
