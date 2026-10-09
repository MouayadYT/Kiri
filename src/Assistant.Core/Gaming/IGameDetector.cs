namespace Assistant.Core.Gaming;

/// <summary>
/// What told the detector that a program is a game. Several can hold at once; the rule that weighs them is the detector's
/// (<c>Assistant.Windows.Gaming.GameClassifier</c>).
/// </summary>
[Flags]
public enum GameEvidence
{
    /// <summary>Nothing says it is a game.</summary>
    None = 0,

    /// <summary>The program is an emulator known by name (Dolphin, RPCS3, RetroArch and the like).</summary>
    KnownEmulator = 1 << 0,

    /// <summary>The program is built on a game engine: its window, its name or the files beside it say so (Unity, Unreal, Godot and the like).</summary>
    GameEngine = 1 << 1,

    /// <summary>The program is in a game store's library, or ships a store's game library beside it (Steam, Epic, GOG and the like).</summary>
    GameStore = 1 << 2,

    /// <summary>Windows itself lists the program as a game (the list its own Game Mode and Game Bar keep).</summary>
    WindowsGameList = 1 << 3,

    /// <summary>The program is in a folder named Games.</summary>
    GamesFolder = 1 << 4,

    /// <summary>Windows reports a Direct3D program in exclusive full screen in front.</summary>
    ExclusiveFullscreen = 1 << 5,

    /// <summary>The program's window covers its whole monitor without a title bar (borderless full screen).</summary>
    Fullscreen = 1 << 6,

    /// <summary>The program itself keeps the graphics card's 3D engine busy, or holds a large share of its memory, and is not playing video.</summary>
    GraphicsLoad = 1 << 7,

    /// <summary>
    /// The program is not a game but a creative app the user asked to have treated as one: a video editor, a compositor, a 3D or rendering tool
    /// or a photo editor, known by name (Premiere Pro, After Effects, DaVinci Resolve, Blender, Photoshop and the like).
    /// </summary>
    CreativeApp = 1 << 8,
}

/// <summary>A game that is running, or a creative app that is open and is being treated as one.</summary>
/// <param name="ProcessId">The id of its process.</param>
/// <param name="Name">What to call it: a game's window title, a creative app's own name, or the program's file name when there is neither.</param>
/// <param name="Evidence">Why it is taken for a game.</param>
public sealed record RunningGame(int ProcessId, string Name, GameEvidence Evidence);

/// <summary>
/// Tells whether a game is running on this PC, for game mode. It watches only between <see cref="Start"/> and <see cref="Stop"/>,
/// by being told when the window in front changes and when a game's process ends, not by looking again and again. It reads nothing a game shows or
/// does: only which program it is, the shape of its window and how hard it drives the graphics card.
/// </summary>
public interface IGameDetector : IDisposable
{
    /// <summary>Whether games and emulators are looked for. Set before <see cref="Start"/>; it is read when watching begins.</summary>
    bool WatchGames { get; set; }

    /// <summary>
    /// Whether creative apps (<see cref="GameEvidence.CreativeApp"/>) are taken for games too while they are open. Set before
    /// <see cref="Start"/>; it is read when watching begins.
    /// </summary>
    bool WatchCreativeApps { get; set; }

    /// <summary>A game that is running now, or <see langword="null"/>. With several running it is the one found first.</summary>
    RunningGame? Current { get; }

    /// <summary>Raised, on a background thread, when <see cref="Current"/> changes between a game and none.</summary>
    event EventHandler? Changed;

    /// <summary>Starts watching. Starting what is already watching changes nothing.</summary>
    void Start();

    /// <summary>Stops watching and forgets the games it found. <see cref="Changed"/> is not raised for that.</summary>
    void Stop();
}
