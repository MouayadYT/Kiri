using System.Collections.Frozen;

namespace Assistant.Windows.Gaming;

/// <summary>
/// What the game detector knows by name: the emulators, the game engines' windows and files, the game stores' folders, the creative apps the
/// user may ask to have treated as games, and the programs that fill the screen or drive the graphics card without being games (browsers,
/// players, editors, launchers, Windows itself). Names are a
/// program's file name without its extension, paths are whole, and both are compared in lower case. The lists only have to be right about
/// what they name: a game that is on none of them is still found by how it behaves (<see cref="GameClassifier"/>).
/// </summary>
internal static class GameCatalog
{
    // Emulators, by the exact name of the program.
    private static readonly FrozenSet<string> Emulators = new[]
    {
        // Multi-system and frontends that run the game in their own window.
        "retroarch", "mednafen", "ares", "emuhawk", "bizhawk", "higan", "bsnes", "bsnes_hd", "mame", "mame64", "mameui64", "scummvm", "86box", "pcem",
        // Nintendo.
        "dolphin", "dolphinqt2", "dolphinwx", "cemu", "yuzu", "suyu", "sudachi", "citron", "eden", "melonds", "mgba", "mgba-sdl", "mgba-qt",
        "citra", "citra-qt", "azahar", "lime3ds", "lime3ds-gui", "lime-qt", "mandarine", "panda3ds", "alber", "fceux", "fceux64", "mesen", "mesen2", "nestopia",
        "punes64", "rmg", "bgb", "bgb64", "sameboy", "gambatte_qt", "skyemu", "no$gba", "zsnes", "zsnesw",
        // Sony.
        "rpcs3", "vita3k", "shadps4", "shadps4-qt", "epsxe", "pcsxr", "pcsx-redux",
        // Microsoft.
        "xemu", "cxbx", "cxbxr-ldr", "xenia", "xenia_canary", "xenia-canary", "xenia_canary_netplay",
        // Sega, arcade and others.
        "flycast", "redream", "demul", "nulldc", "kronos", "yabause", "yabasanshiro", "blastem", "gens", "supermodel", "teknoparrotui", "openbor",
        // Android.
        "hd-player", "dnplayer", "ldplayer", "nox", "noxvmhandle", "memu", "memuheadless", "mumuplayer", "mumunxdevice", "mumuvmmheadless",
        "androidemulatoren", "androidemulatorex", "crosvm",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Emulators whose program's name carries a version, a build or a processor after it (pcsx2-qtx64-avx2, duckstation-qt-x64-ReleaseLTCG).
    private static readonly string[] EmulatorPrefixes =
    [
        "pcsx2", "duckstation", "ppsspp", "desmume", "snes9x", "ryujinx", "dosbox", "project64", "visualboyadvance", "simple64", "mupen64",
        "dolphin-", "retroarch-", "xenia_", "mame-", "citra-", "lime3ds-", "azahar-", "m64p", "vba-m",
    ];

    // Games that are neither in a store's folder nor on an engine this file knows: by name.
    private static readonly FrozenSet<string> Games = new[]
    {
        "robloxplayerbeta", "minecraft.windows", "osu!", "genshinimpact", "yuanshen", "starrail", "zenlesszonezero", "bh3",
        "gta5", "gta5_enhanced", "rdr2", "ffxiv_dx11", "ffxiv", "wow", "wowclassic", "eldenring", "r5apex", "r5apex_dx12", "cs2", "dota2", "tslgame",
        "league of legends", "overwatch", "destiny2", "warframe.x64", "pathofexile", "pathofexile_x64", "pathofexilesteam",
        "rocketleague", "cod", "modernwarfare", "escapefromtarkov", "starcitizen", "worldoftanks", "worldofwarships", "hearthstone",
        "diablo iv", "diablo iii64", "sc2_x64", "albion-online", "runelite", "osclient", "rs2client", "growtopia", "geometrydash", "terraria",
    }.ToFrozenSet(StringComparer.Ordinal);

    // The window class a game engine gives its player's window.
    private static readonly FrozenSet<string> EngineWindowClasses = new[]
    {
        "UnityWndClass", "UnrealWindow", "LaunchUnrealUWindowsClient", "YYGameMakerYY", "Valve001", "CryENGINE", "grcWindow", "RiotWindowClass",
        "TankWindowClass", "GxWindowClass", "POEWindowClass", "DagorWClass", "RGSS Player", "Respawn001", "LWJGL",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    // What a game engine's shipped build is named after: an Unreal game is <Name>-Win64-Shipping.exe.
    private static readonly string[] EngineNameSuffixes = ["-win64-shipping", "-wingdk-shipping", "-win64-test", "-eossdk-win64-shipping", "-win32-shipping"];

    // Folders a game store installs games into. A program below one of them is a game unless the catalog names it as something else.
    private static readonly string[] StoreFolders =
    [
        @"\steamapps\common\", @"\steamapps\sourcemods\", @"\epic games\", @"\gog games\", @"\gog galaxy\games\", @"\ubisoft game launcher\games\",
        @"\ea games\", @"\origin games\", @"\riot games\", @"\xboxgames\", @"\rockstar games\", @"\amazon games\library\", @"\itch\apps\",
        @"\_retail_\", @"\_classic_\", @"\_classic_era_\", @"\_ptr_\", @"\roblox\versions\", @"\modifiablewindowsapps\",
    ];

    // What is in those folders, or beside games, and is not a game: the stores' own programs, engines' editors, and software sold in a game store.
    private static readonly string[] NeverFolders =
    [
        @":\windows\", @"\epic games\launcher\", @"\epic games\ue_", @"\epic games\epic online services\", @"\epic games\directxredist\",
        @"\riot games\riot client\", @"\rockstar games\launcher\", @"\rockstar games\social club\", @"\steamapps\common\wallpaper_engine\",
        @"\steamapps\common\steamworks shared\", @"\steamapps\common\steam controller configs\", @"\steamapps\common\soundpad\",
        @"\steamapps\common\vtube studio\", @"\steamapps\common\rpg maker", @"\steamapps\common\blender\", @"\steamapps\common\obs studio\",
        @"\steamapps\common\aseprite\", @"\steamapps\common\lossless scaling\", @"\steamapps\common\borderless gaming\",
        @"\steamapps\common\substance", @"\steamapps\common\godot engine\", @"\microsoft\edgewebview\", @"\_commonredist\", @"\redist\", @"\redistributables\",
    ];

    // Programs that are never a game, however much of the screen or of the graphics card they take.
    private static readonly FrozenSet<string> NeverNames = new[]
    {
        // Windows itself.
        "explorer", "searchhost", "searchapp", "startmenuexperiencehost", "shellexperiencehost", "lockapp", "logonui", "dwm", "textinputhost",
        "applicationframehost", "systemsettings", "taskmgr", "consent", "sihost", "screenclippinghost", "snippingtool", "widgets", "widgetboard",
        "gamebar", "gamebarftserver", "xboxpcapp", "xboxpcappft", "gamingservices", "conhost", "openconsole", "windowsterminal", "cmd", "powershell", "pwsh",
        "mstsc", "msrdc", "vmconnect", "magnify", "narrator", "osk", "photos", "microsoft.photos", "mspaint", "notepad", "calculatorapp", "winstore.app",
        // Browsers, and the apps built on one (their drawing is done by a separate process, so it is never theirs).
        "chrome", "msedge", "msedgewebview2", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "zen", "librewolf", "waterfox", "floorp", "thorium",
        "chromium", "iexplore", "duckduckgo", "tor", "discord", "discordptb", "discordcanary", "slack", "teams", "ms-teams", "zoom", "skype", "spotify",
        "whatsapp", "telegram", "signal", "notion", "obsidian", "figma", "postman", "claude", "chatgpt",
        // Video and music players, and streaming a screen from somewhere else.
        "vlc", "mpv", "mpvnet", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayer64", "potplayermini", "potplayermini64", "wmplayer",
        "video.ui", "microsoft.media.player", "kodi", "plex", "plex htpc", "plexmediaplayer", "jellyfinmediaplayer", "stremio", "smplayer", "kmplayer",
        "kmplayer64", "gom", "5kplayer", "mediaplayerclassic", "zoomplayer", "netflix", "primevideo", "disneyplus", "itunes", "foobar2000", "aimp", "winamp",
        "geforcenow", "moonlight", "parsecd", "steamlink", "sunshine", "rustdesk", "anydesk", "teamviewer", "chiaki", "chiaki-ng", "xbox", "xcloud",
        "vmware", "vmware-vmx", "vmplayer", "virtualbox", "virtualboxvm", "qemu-system-x86_64", "duet", "spacedesk", "displayfusion",
        // Recording, streaming and what sits over a game.
        "obs64", "obs32", "obs", "streamlabs obs", "streamlabs desktop", "xsplit.core", "nvidia app", "nvidia overlay", "nvidia share", "nvcontainer",
        "nvidia broadcast", "radeonsoftware", "amdrssrcext", "msiafterburner", "rtss", "hwinfo64", "hwinfo32", "cpuz", "gpu-z", "losslessscaling",
        "borderlessgaming", "wallpaper32", "wallpaper64", "lively", "rainmeter", "medal", "overwolf", "playnite.desktopapp", "playnite.fullscreenapp",
        // Office, reading and presenting.
        "powerpnt", "winword", "excel", "onenote", "outlook", "msaccess", "acrobat", "acrord32", "sumatrapdf", "soffice.bin", "soffice", "foxitpdfreader",
        // Making things that asks little of the graphics card: sound, pixels, layout, code. (The heavy creative apps are a list of their own below.)
        "unity hub", "unityhub", "unrealeditor-cmd", "indesign", "aseprite", "paintdotnet", "audacity", "adobe audition", "fl64", "reaper",
        "creative cloud", "creative cloud ui helper", "adobe desktop service", "ccxprocess", "coresync", "blender-launcher",
        "rpgmv", "rpgmz", "rpgvxace", "gamemaker", "gamemakerstudio", "construct3", "tiled", "vtube studio",
        "devenv", "code", "code - insiders", "cursor", "windsurf", "zed", "rider64", "idea64", "pycharm64", "clion64", "webstorm64", "studio64", "goland64",
        "phpstorm64", "datagrip64", "rustrover64", "sublime_text", "notepad++", "robloxstudiobeta", "robloxstudio",
        // Game stores and their launchers: where games are started from, not games.
        "steam", "steamwebhelper", "steamservice", "epicgameslauncher", "epicwebhelper", "unrealcefsubprocess", "eadesktop", "ealauncher", "eabackgroundservice",
        "origin", "upc", "ubisoftconnect", "uplaywebcore", "battle.net", "battle.net helper", "galaxyclient", "galaxyclient helper", "riotclientservices",
        "riotclientux", "riotclientuxrender", "riotclientcrashhandler", "leagueclient", "leagueclientux", "leagueclientuxrender", "heroic", "itch",
        "amazon games", "amazon games ui", "bethesdanetlauncher", "rockstarservice", "launcherpatcher", "minecraftlauncher", "minecraft", "lunar client",
        "badlion client", "tlauncher", "prismlauncher", "multimc", "curseforge", "modrinth app", "vortex", "modorganizer", "r2modman", "thunderstore mod manager",
        // What runs beside a game and has a window of its own for a moment.
        "unitycrashhandler64", "unitycrashhandler32", "crashreportclient", "crashpad_handler", "bssndrpt64", "bssndrpt", "werfault", "dxsetup",
        "vc_redist.x64", "vc_redist.x86", "vcredist_x64", "vcredist_x86", "ue4prereqsetup_x64", "ueprereqsetup_x64", "dotnetfx", "installer", "setup",
        "uninstall", "unins000", "qtwebengineprocess", "cefsharp.browsersubprocess", "handy",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Editors whose program's name carries a version (Godot_v4.3-stable_win64, gimp-2.10).
    private static readonly string[] NeverPrefixes = ["godot_v", "godot.", "gimp-", "assistant.", "jetbrains", "ableton live", "adobe "];

    // Creative apps that work the graphics card and its memory the way a game does: video editing and compositing, 3D, rendering and CAD, and
    // photo and painting. They are never games. When the user asks for it they are treated as one while they are open; otherwise they are left alone.
    private static readonly FrozenSet<string> CreativeApps = new[]
    {
        // Video editing, compositing and encoding.
        "adobe premiere pro", "adobe premiere pro (beta)", "adobe premiere elements", "afterfx", "afterfx (beta)", "adobe media encoder",
        "adobe media encoder (beta)", "character animator", "resolve", "capcut", "filmora", "wondershare filmora", "kdenlive", "shotcut", "handbrake",
        "avidmediacomposer", "edius", "hitfilm", "hitfilm pro", "olive-editor", "topaz video ai", "topaz photo ai", "topaz gigapixel ai",
        // 3D, rendering, game engines' editors and CAD.
        "blender", "cinema 4d", "maya", "3dsmax", "houdini", "houdinifx", "houdinicore", "hindie", "happrentice", "zbrush", "toolbag", "keyshot",
        "unrealeditor", "ue4editor", "unity", "twinmotion", "lumion", "d5 render", "octane", "modo", "substance 3d painter", "substance 3d designer",
        "substance 3d sampler", "substance 3d stager", "substance painter", "substance designer", "marmoset toolbag", "acad", "sldworks", "fusion360",
        "sketchup", "rhino", "revit", "inventor", "archicad",
        // Photo and painting.
        "photoshop", "illustrator", "lightroom", "lightroomclassic", "krita", "clipstudiopaint", "captureone", "affinity", "affinity photo 2",
        "affinity designer 2", "affinity publisher 2", "dxo.photolab",
    }.ToFrozenSet(StringComparer.Ordinal);

    // Creative apps whose program's name carries a version or an edition (vegas200, Nuke15.1, MarvelousDesigner12_Personal_x64, Adobe Substance 3D Painter).
    private static readonly string[] CreativePrefixes = ["vegas", "nuke", "marvelousdesigner", "adobe substance 3d", "luminar"];

    /// <summary>
    /// Whether the program is a creative app that works the graphics card as a game does (Premiere Pro, After Effects, DaVinci Resolve, Blender,
    /// Photoshop and the like). Such a program is never a game; whether it is treated as one is the user's choice.
    /// </summary>
    public static bool IsCreativeApp(string name)
    {
        if (CreativeApps.Contains(name))
        {
            return true;
        }

        foreach (var prefix in CreativePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the program is one the catalog knows is not a game.</summary>
    /// <param name="name">The program's name in lower case, without its extension; empty when it could not be read.</param>
    /// <param name="path">The program's whole path in lower case; empty when it could not be read.</param>
    public static bool IsNeverAGame(string name, string path)
    {
        if (NeverNames.Contains(name))
        {
            return true;
        }

        foreach (var prefix in NeverPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        foreach (var folder in NeverFolders)
        {
            if (path.Contains(folder, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the program is an emulator the catalog knows.</summary>
    public static bool IsEmulator(string name)
    {
        if (Emulators.Contains(name))
        {
            return true;
        }

        foreach (var prefix in EmulatorPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the program is a game the catalog knows by name, or is named as a game engine names what it ships.</summary>
    public static bool IsGameByName(string name)
    {
        if (Games.Contains(name) || name is "ue4game" or "ue4-win64-shipping")
        {
            return true;
        }

        foreach (var suffix in EngineNameSuffixes)
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the window is a game engine's. Minecraft and the games built like it are Java drawing through LWJGL, in a window of the GLFW library:
    /// that window class alone is used by tools too, so it counts only for a Java program.
    /// </summary>
    public static bool IsEngineWindow(string windowClass, string name) =>
        EngineWindowClasses.Contains(windowClass)
        || (name is "javaw" or "java" && windowClass is "GLFW30" or "LWJGL");

    /// <summary>Whether the program is below a folder a game store installs games into.</summary>
    public static bool IsInStoreLibrary(string path)
    {
        foreach (var folder in StoreFolders)
        {
            if (path.Contains(folder, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether one of the folders the program is in is named Games (D:\Games\...), which says what the user keeps there and no more.</summary>
    public static bool IsInGamesFolder(string path) =>
        path.Contains(@"\games\", StringComparison.Ordinal) || path.Contains(@"\game\", StringComparison.Ordinal)
        || path.Contains(@"\my games\", StringComparison.Ordinal) || path.Contains(@"\juegos\", StringComparison.Ordinal)
        || path.Contains(@"\spiele\", StringComparison.Ordinal) || path.Contains(@"\jeux\", StringComparison.Ordinal);
}
