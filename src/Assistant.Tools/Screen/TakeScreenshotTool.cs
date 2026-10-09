using System.Text.Json;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Tools;

namespace Assistant.Tools.Screen;

/// <summary>
/// <c>take_screenshot</c> (PROJECT_SPEC §4.6, §4.8): takes a picture of the screen the user is on, without the Assistant's own windows, and
/// attaches it to the conversation, where the user sees it as a chip like any part of the screen they asked about, and can take it off.
/// It lives in memory only and is never saved (P7), and what the model gets back is that it was taken and how big it is, never the
/// picture: the model reads its words with <c>read_screen_text</c>, and the user's next question is asked about it. It has a side effect and
/// needs the Screen Capture permission, so the user confirms each call and it does nothing while the permission is off.
/// </summary>
public sealed class TakeScreenshotTool(IScreenshotTaker screenshots, IScreenText? screens = null) : ITool
{
    /// <inheritdoc/>
    public ToolDefinition Definition { get; } = ToolDefinition.Create(
        SystemToolResults.TakeScreenshot,
        "Take a picture of the screen the user is on and attach it to this conversation. Use it only when the user asks you to look at " +
        "or capture their screen. You cannot see the picture in this answer; read its words with read_screen_text, and the user can ask " +
        "about it next.",
        [],
        RiskLevel.SideEffect,
        PermissionCapability.ScreenCapture,
        TimeSpan.FromSeconds(20));

    /// <inheritdoc/>
    /// <remarks>
    /// A question that came with a picture of its own ("what does this say?" with a picture pasted) is about that picture: the tool is not offered
    /// then, so the user is not asked for a picture of their whole screen that nobody needs.
    /// </remarks>
    public bool IsOffered(ToolContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return !context.HasPicture;
    }

    /// <inheritdoc/>
    public Task<ToolPlan> PlanAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(ToolPlan.Do(
            token => RunAsync(call, arguments, context, token),
            new ToolConfirmation(
                ConfirmationKind.Capture,
                "Take a picture of your screen?",
                [
                    new ConfirmationDetail("What", "A picture of the screen you are on, without the Assistant's own windows"),
                    new ConfirmationDetail("Where it goes", "Attached to this conversation, in memory only. It is not saved, and you can take it off."),
                ],
                "Take picture")));

    /// <inheritdoc/>
    public async Task<ToolResult> RunAsync(ToolCall call, JsonElement arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var outcome = await screenshots.TakeAsync(context.ConversationId, cancellationToken).ConfigureAwait(false);
        if (outcome.Status != ScreenshotStatus.Taken)
        {
            return ToolErrors.Result(
                call,
                ToolResultStatus.Failed,
                ToolErrors.Failed,
                "The screen could not be captured: Windows does not allow it while the PC is locked or a secure prompt is showing. Tell the user.");
        }

        var message = screens is { IsAvailable: true }
            ? "A screenshot was taken and attached to this conversation, and the user can see it. You cannot see it in this answer: " +
              "use read_screen_text to read the words on it."
            : "A screenshot was taken and attached to this conversation, and the user can see it. You cannot see it in this answer: " +
              "tell the user they can ask about it next.";
        return new ToolResult(
            call.Id, call.ToolName, ToolResultStatus.Succeeded, SystemToolResults.Screenshot(outcome.Width, outcome.Height, message));
    }
}
