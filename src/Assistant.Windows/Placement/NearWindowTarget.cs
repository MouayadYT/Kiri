namespace Assistant.Windows.Placement;

/// <summary>
/// Another application's window that an overlay is to open next to, such as the browser a selection was made in, as it was at the
/// moment it was noted. It names where the window and the pointer were, never what the window shows or is called, so it is safe to
/// log (PROJECT_SPEC §3.2).
/// </summary>
/// <param name="Window">The window's handle, which is only good while that window exists.</param>
/// <param name="ProcessId">The id of the window's process.</param>
/// <param name="Bounds">The window as it is seen (without the invisible resize border), in physical screen pixels.</param>
/// <param name="Pointer">Where the pointer was, in physical screen pixels, or <see langword="null"/> if Windows would not say.</param>
public sealed record NearWindowTarget(nint Window, int ProcessId, ScreenRect Bounds, ScreenPoint? Pointer);
