using Assistant.Core.Gaming;

namespace Assistant.Windows.Gaming;

/// <summary>What the files beside a program say about it (<see cref="GameFileProbe"/>).</summary>
[Flags]
internal enum GameFiles
{
    None = 0,

    /// <summary>A game engine's player or data is there (Unity, Godot, GameMaker, RPG Maker, Ren'Py and the like), or an anti-cheat.</summary>
    Engine = 1,

    /// <summary>A game store's library for games is there (Steam, Epic Online Services, GOG Galaxy, Ubisoft, Xbox).</summary>
    Store = 2,
}

/// <summary>How much one process used the graphics card over the last few seconds.</summary>
/// <param name="Render3DPercent">Its share of the 3D engine's time, 0 to 100: what drawing a game takes.</param>
/// <param name="VideoDecodePercent">Its share of the video decoder's time, 0 to 100: what playing a video takes.</param>
/// <param name="DedicatedBytes">The graphics memory it holds on a graphics card of its own.</param>
/// <param name="SharedBytes">The memory it holds for graphics out of the PC's own, which is where a built-in graphics chip keeps everything.</param>
internal readonly record struct GpuUsage(double Render3DPercent, double VideoDecodePercent, long DedicatedBytes, long SharedBytes = 0);

/// <summary>What was seen of a window and the program behind it. Nothing in it is the window's content.</summary>
internal readonly record struct GameFacts
{
    /// <summary>The program's path, or empty when Windows would not say (a protected process).</summary>
    public string ExecutablePath { get; init; }

    /// <summary>The window's class.</summary>
    public string WindowClass { get; init; }

    /// <summary>The window's title.</summary>
    public string Title { get; init; }

    /// <summary>Whether the window covers the whole monitor it is on, taskbar included.</summary>
    public bool CoversMonitor { get; init; }

    /// <summary>Whether the window has a title bar. A maximized window has one; a borderless game does not.</summary>
    public bool HasCaption { get; init; }

    /// <summary>Whether the window is in front and Windows reports a Direct3D program in exclusive full screen.</summary>
    public bool ExclusiveFullscreen { get; init; }

    /// <summary>Whether Windows' own list of games has the program.</summary>
    public bool InWindowsGameList { get; init; }

    /// <summary>What the files beside the program say.</summary>
    public GameFiles Files { get; init; }

    /// <summary>Whether the process kept the graphics card busy over several looks in a row (<see cref="GameClassifier.IsGraphicsLoad"/>).</summary>
    public bool SustainedGraphicsLoad { get; init; }

    /// <summary>What to call the program: the window's title, or the program's name when the window has none.</summary>
    public string DisplayName
    {
        get
        {
            var title = Title?.Trim() ?? "";
            if (title.Length == 0)
            {
                title = Path.GetFileNameWithoutExtension(ExecutablePath ?? "");
            }

            return title.Length > 60 ? title[..60] : title;
        }
    }
}

/// <summary>What the classifier makes of a window.</summary>
internal enum GameVerdictKind
{
    /// <summary>The program is one that is never a game. It is not looked at again.</summary>
    Never,

    /// <summary>Nothing says it is a game. It is looked at again, cheaply, in case its window changes.</summary>
    NotYet,

    /// <summary>Something says it may be a game: how it uses the graphics card decides.</summary>
    Watch,

    /// <summary>It is a game.</summary>
    Game,
}

/// <summary>A verdict and what it rests on.</summary>
internal readonly record struct GameVerdict(GameVerdictKind Kind, GameEvidence Evidence)
{
    public bool IsGame => Kind == GameVerdictKind.Game;
}

/// <summary>
/// Decides whether a window is a game's, for game mode. No single sign is trusted with everything, because each is wrong
/// somewhere: a full-screen window may be a film or a slide show, a busy graphics card may be an editor, and a game may be played in a window.
/// So the signs are of three kinds, and the rule is about how they meet:
/// <list type="bullet">
/// <item><b>What the program is</b>: an emulator known by name, a program built on a game engine, or one in a game store's library. Any one of
/// these is enough, whatever its window looks like, so a game in a window is found too.</item>
/// <item><b>A hint</b>: Windows' own list of games has it, or it is in a folder named Games. A hint is enough once the program also behaves like
/// a game: it fills the screen, or keeps the graphics card busy.</item>
/// <item><b>How it behaves</b>: with nothing known about it, a program is a game when it both fills the screen and keeps the graphics card busy
/// itself, without decoding video. Browsers and the apps built on them draw in a separate process and players decode video, so neither
/// passes; the catalog's list of programs that are never games covers the rest.</item>
/// </list>
/// Creative apps are a case of their own: they are never games, and whether one that is open is treated as a game is the user's choice, by name.
/// </summary>
internal static class GameClassifier
{
    /// <summary>A program whose share of the 3D engine stays at or above this is drawing like a game.</summary>
    internal const double Render3DPercent = 10;

    /// <summary>A program that also decodes this much video is playing a video, whatever else it draws.</summary>
    internal const double VideoDecodePercent = 2;

    /// <summary>A program that holds this much graphics memory itself is one a game's models compete with.</summary>
    internal const long DedicatedBytes = 1L << 30;

    /// <summary>A program that draws less than this, and holds less than <see cref="StillHeldBytes"/>, is not using the graphics card any more.</summary>
    internal const double StillDrawingPercent = 3;

    /// <summary>See <see cref="StillDrawingPercent"/>.</summary>
    internal const long StillHeldBytes = 256L << 20;

    private const GameEvidence Identity =
        GameEvidence.KnownEmulator | GameEvidence.GameEngine | GameEvidence.GameStore | GameEvidence.CreativeApp;
    private const GameEvidence Hint = GameEvidence.WindowsGameList | GameEvidence.GamesFolder;
    private const GameEvidence Behavior = GameEvidence.Fullscreen | GameEvidence.GraphicsLoad;

    /// <summary>Whether one reading of the graphics card is what a game makes: it draws, or holds a lot of graphics memory, and decodes no video.</summary>
    public static bool IsGraphicsLoad(GpuUsage usage) =>
        usage.VideoDecodePercent < VideoDecodePercent
        && (usage.Render3DPercent >= Render3DPercent || usage.DedicatedBytes >= DedicatedBytes);

    /// <summary>
    /// Whether a program that was taken for a game is still using the graphics card: it draws, or it holds graphics memory. A game the user has
    /// switched away from draws little and still holds everything it loaded; a program that only filled the screen for a while holds nothing.
    /// </summary>
    public static bool IsStillUsingGraphics(GpuUsage usage) =>
        usage.Render3DPercent >= StillDrawingPercent || usage.DedicatedBytes + usage.SharedBytes >= StillHeldBytes;

    /// <summary>
    /// Whether the verdict rests on anything but how the program behaved for a few seconds: what it is, or a hint about it. A game known only by
    /// its behavior is the one guess the detector makes, so it is the one it keeps checking (<see cref="GameDetector"/>).
    /// </summary>
    public static bool IsKnown(GameEvidence evidence) => (evidence & (Identity | Hint)) != 0;

    /// <summary>Weighs what was seen of a window.</summary>
    /// <param name="facts">What was seen.</param>
    /// <param name="games">Whether games and emulators are looked for at all. When they are not, nothing but a creative app is ever a verdict.</param>
    /// <param name="creativeApps">
    /// Whether a creative app (Premiere Pro, After Effects, Blender and the like) is treated as a game while it is open. Such an app is known by
    /// its name alone and is never measured: when this is off it is simply left alone, however hard it works the graphics card.
    /// </param>
    public static GameVerdict Classify(in GameFacts facts, bool games = true, bool creativeApps = false)
    {
        var path = (facts.ExecutablePath ?? "").ToLowerInvariant();
        var name = Path.GetFileNameWithoutExtension(path);
        if (GameCatalog.IsCreativeApp(name))
        {
            return creativeApps
                ? new GameVerdict(GameVerdictKind.Game, GameEvidence.CreativeApp)
                : new GameVerdict(GameVerdictKind.Never, GameEvidence.None);
        }

        if (!games || GameCatalog.IsNeverAGame(name, path))
        {
            return new GameVerdict(GameVerdictKind.Never, GameEvidence.None);
        }

        var evidence = GameEvidence.None;
        if (GameCatalog.IsEmulator(name))
        {
            evidence |= GameEvidence.KnownEmulator;
        }

        if (GameCatalog.IsGameByName(name) || GameCatalog.IsEngineWindow(facts.WindowClass ?? "", name) || facts.Files.HasFlag(GameFiles.Engine))
        {
            evidence |= GameEvidence.GameEngine;
        }

        if (GameCatalog.IsInStoreLibrary(path) || facts.Files.HasFlag(GameFiles.Store))
        {
            evidence |= GameEvidence.GameStore;
        }

        if (facts.InWindowsGameList)
        {
            evidence |= GameEvidence.WindowsGameList;
        }

        if (GameCatalog.IsInGamesFolder(path))
        {
            evidence |= GameEvidence.GamesFolder;
        }

        // Exclusive full screen is a state of the whole screen, and for a moment after the user switches away it may still be reported while another
        // window is already in front. So it only speaks for a window that covers the monitor itself: there it stands in for the missing title bar.
        if (facts.CoversMonitor && (!facts.HasCaption || facts.ExclusiveFullscreen))
        {
            evidence |= GameEvidence.Fullscreen;
            if (facts.ExclusiveFullscreen)
            {
                evidence |= GameEvidence.ExclusiveFullscreen;
            }
        }

        if (facts.SustainedGraphicsLoad)
        {
            evidence |= GameEvidence.GraphicsLoad;
        }

        return new GameVerdict(Decide(evidence), evidence);
    }

    private static GameVerdictKind Decide(GameEvidence evidence)
    {
        var identity = (evidence & Identity) != 0;
        var hint = (evidence & Hint) != 0;
        var fullscreen = (evidence & GameEvidence.Fullscreen) != 0;
        var load = (evidence & GameEvidence.GraphicsLoad) != 0;
        if (identity || (hint && (evidence & Behavior) != 0) || (fullscreen && load))
        {
            return GameVerdictKind.Game;
        }

        // A hint or a full screen is worth reading the graphics card for; anything else is only worth another look at its window later.
        return hint || fullscreen ? GameVerdictKind.Watch : GameVerdictKind.NotYet;
    }
}
