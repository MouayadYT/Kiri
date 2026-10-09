using System.Collections.Concurrent;

namespace Assistant.Windows.Gaming;

/// <summary>
/// Looks at the names of the files beside a program for what a game ships with: an engine's player or data, an anti-cheat, a game store's
/// library. It asks only whether a file or folder of that name is there, opens none of them, and remembers the answer for each program, so a
/// program is looked at once.
/// </summary>
internal static class GameFileProbe
{
    // Enough for every program a user has in front in a session; past it the answers are forgotten and found again.
    private const int MaxRemembered = 512;

    private static readonly ConcurrentDictionary<string, GameFiles> Remembered = new(StringComparer.OrdinalIgnoreCase);

    // Beside the program: an engine's player or its data. Only what a game ships and little else does: libraries that editors and renderers carry
    // too (upscalers, physics, sound) are left out, because what is found here is taken for a game without anything else being asked.
    private static readonly string[] EngineFiles =
    [
        "UnityPlayer.dll", "GameAssembly.dll", "data.win", "Game.rgss3a", "Game.rgss2a", "Game.rgssad", "love.dll", "FNA.dll", "MonoGame.Framework.dll",
    ];

    // Beside the program: an anti-cheat, or an engine's own folder.
    private static readonly string[] EngineFolders = ["EasyAntiCheat", "BattlEye", "renpy", "MonoBleedingEdge"];

    // Beside the program: a game store's library for games.
    private static readonly string[] StoreFiles =
    [
        "steam_api64.dll", "steam_api.dll", "steam_appid.txt", "EOSSDK-Win64-Shipping.dll", "EOSSDK-Win32-Shipping.dll", "Galaxy64.dll", "Galaxy.dll",
        "uplay_r1_loader64.dll", "uplay_r2_loader64.dll", "upc_r1_loader64.dll", "upc_r2_loader64.dll", "MicrosoftGame.config",
    ];

    /// <summary>What the files beside <paramref name="executablePath"/> say. Never throws: a folder that cannot be read says nothing.</summary>
    public static GameFiles Read(string executablePath)
    {
        if (string.IsNullOrEmpty(executablePath))
        {
            return GameFiles.None;
        }

        if (Remembered.TryGetValue(executablePath, out var known))
        {
            return known;
        }

        var found = Look(executablePath);
        if (Remembered.Count >= MaxRemembered)
        {
            Remembered.Clear();
        }

        Remembered[executablePath] = found;
        return found;
    }

    private static GameFiles Look(string executablePath)
    {
        try
        {
            var folder = Path.GetDirectoryName(executablePath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return GameFiles.None;
            }

            var name = Path.GetFileNameWithoutExtension(executablePath);
            var found = GameFiles.None;
            if (EngineFiles.Any(file => File.Exists(Path.Combine(folder, file)))
                || EngineFolders.Any(directory => Directory.Exists(Path.Combine(folder, directory)))
                // Unity keeps a game's data in <Name>_Data, Godot in <Name>.pck, and RPG Maker MV and MZ in a web page's folders.
                || Directory.Exists(Path.Combine(folder, name + "_Data"))
                || File.Exists(Path.Combine(folder, name + ".pck"))
                || File.Exists(Path.Combine(folder, "www", "js", "rpg_core.js"))
                || File.Exists(Path.Combine(folder, "js", "rmmz_core.js")))
            {
                found |= GameFiles.Engine;
            }

            if (StoreFiles.Any(file => File.Exists(Path.Combine(folder, file))))
            {
                found |= GameFiles.Store;
            }

            return found;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return GameFiles.None;
        }
    }
}
