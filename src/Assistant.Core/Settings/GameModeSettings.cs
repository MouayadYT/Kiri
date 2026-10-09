namespace Assistant.Core.Settings;

/// <summary>
/// Game mode: while a game runs, or a creative app when the user wants that too, the Assistant lets go of every AI model it holds (the local
/// model, the voice, speech recognition and the wake word), so the other program has the graphics memory and the processor to itself, and takes
/// them up again when it ends. The two kinds are chosen separately; with both off nothing watches, so it costs nothing.
/// </summary>
public sealed record GameModeSettings
{
    /// <summary>
    /// Whether the models are released while a game or an emulator runs. Off until the user turns it on: setup asks, and Settings > General has
    /// the switch.
    /// </summary>
    public bool Games { get; init; }

    /// <summary>
    /// Whether the models are released while a creative app is open too: the video editors, compositors, 3D and rendering tools and photo editors
    /// that need the same graphics memory (Premiere Pro, After Effects, DaVinci Resolve, Blender, Photoshop and the like). Off until the user
    /// turns it on, in setup or in Settings > General.
    /// </summary>
    public bool CreativeApps { get; init; }

    /// <summary>
    /// Whether a recognizer the Assistant shares with another app (Handy) is released too: while the models are released and that app holds a
    /// model, it is closed, and it is started again afterwards. Handy has no command that unloads its model, so closing it is the only way.
    /// </summary>
    public bool ReleaseSharedRecognizer { get; init; } = true;
}
