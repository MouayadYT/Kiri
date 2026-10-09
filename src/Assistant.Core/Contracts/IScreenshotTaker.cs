namespace Assistant.Core.Contracts;

/// <summary>How a screenshot taken for a conversation ended.</summary>
public enum ScreenshotStatus
{
    /// <summary>A picture of the screen was taken and attached to the conversation.</summary>
    Taken = 0,

    /// <summary>
    /// The screen could not be captured: Windows does not allow it while the PC is locked or a secure prompt is showing, there is no
    /// monitor, or the capture failed.
    /// </summary>
    Failed = 1,
}

/// <summary>What <see cref="IScreenshotTaker"/> did.</summary>
/// <param name="Status">How it ended.</param>
/// <param name="Width">The picture's width in pixels when it was taken; otherwise 0.</param>
/// <param name="Height">The picture's height in pixels when it was taken; otherwise 0.</param>
public sealed record ScreenshotOutcome(ScreenshotStatus Status, int Width = 0, int Height = 0);

/// <summary>
/// Takes a picture of the screen the user is working on, for the conversation the model is answering in (PROJECT_SPEC §4.6, §4.8): the
/// <c>take_screenshot</c> tool. The picture lives in memory only, is shown to the user as an attachment of the conversation like any
/// other part of the screen they asked about, goes with the conversation's next questions until they take it off, and is never saved,
/// logged or sent anywhere (P7). It does not ask whether the Screen Capture permission is on, and it is not asked for the user's
/// confirmation: the tool's executor has done both, since a model's call has no other way to reach it.
/// </summary>
public interface IScreenshotTaker
{
    /// <summary>
    /// Captures the monitor the user is on without the Assistant's own windows, and attaches the picture to the conversation
    /// <paramref name="conversationId"/>. A failure is a <see cref="ScreenshotStatus.Failed"/> outcome, not an exception.
    /// </summary>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    Task<ScreenshotOutcome> TakeAsync(Guid conversationId, CancellationToken cancellationToken = default);
}
