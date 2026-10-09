namespace Assistant.Windows.Capture;

/// <summary>Why a capture could not be made.</summary>
public enum ScreenCaptureFailure
{
    /// <summary>The area asked for holds no part of the screen, or has no size.</summary>
    NothingToCapture = 0,

    /// <summary>The window is gone, or minimized, so it has no picture to take.</summary>
    WindowUnavailable = 1,

    /// <summary>The area has more pixels than a capture may hold (<see cref="ScreenCaptureService.MaxPixels"/>).</summary>
    TooLarge = 2,

    /// <summary>
    /// Windows refused to copy the screen, as it does while the PC is locked or a secure desktop (such as the one for UAC
    /// prompts) is showing.
    /// </summary>
    Refused = 3,

    /// <summary>The Screen Capture permission does not allow it (step 119): it is off, or set to ask every time and this use was not asked about. Nothing was captured.</summary>
    NotAllowed = 4,
}

/// <summary>
/// A capture that could not be made. It names a <see cref="Failure"/> and, when Windows gave one, its error code, and never
/// what was on the screen or the title of a window (PROJECT_SPEC §3.3), so it is safe to log and to show.
/// </summary>
public sealed class ScreenCaptureException : Exception
{
    /// <summary>Creates the exception for <paramref name="failure"/>.</summary>
    public ScreenCaptureException(ScreenCaptureFailure failure, int errorCode = 0)
        : base(Describe(failure))
    {
        Failure = failure;
        ErrorCode = errorCode;
    }

    /// <summary>Why the capture could not be made.</summary>
    public ScreenCaptureFailure Failure { get; }

    /// <summary>The Win32 error code Windows gave, or 0 when it gave none.</summary>
    public int ErrorCode { get; }

    private static string Describe(ScreenCaptureFailure failure) => failure switch
    {
        ScreenCaptureFailure.NothingToCapture => "There is nothing on the screen to capture there.",
        ScreenCaptureFailure.WindowUnavailable => "That window can't be captured: it is gone or minimized.",
        ScreenCaptureFailure.TooLarge => "That area is too large to capture.",
        ScreenCaptureFailure.NotAllowed => "Screen Capture is not allowed in Settings, under Permissions.",
        _ => "Windows did not allow the screen to be captured.",
    };
}
