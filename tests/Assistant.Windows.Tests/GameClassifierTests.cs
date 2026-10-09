using Assistant.Core.Gaming;
using Assistant.Windows.Gaming;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// The rule that decides whether a window is a game's (game mode). It is tried on what windows look like to the detector: a path, a window class,
/// whether it fills the screen and what the graphics card says. No window is opened and no game is needed.
/// </summary>
public sealed class GameClassifierTests
{
    private static readonly GameFacts Windowed = new() { WindowClass = "SomeWindow", Title = "", HasCaption = true };

    private static GameFacts Borderless(GameFacts facts) => facts with { CoversMonitor = true, HasCaption = false };

    [Theory]
    [InlineData(@"D:\Program files\Dolphin-x64\Dolphin.exe")]
    [InlineData(@"D:\Program files\RPCS3\rpcs3.exe")]
    [InlineData(@"D:\Program files\Xenia\xenia_canary.exe")]
    [InlineData(@"C:\Emulators\PCSX2\pcsx2-qtx64-avx2.exe")]
    [InlineData(@"C:\Emulators\duckstation-qt-x64-ReleaseLTCG.exe")]
    [InlineData(@"C:\RetroArch-Win64\retroarch.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Ryujinx\Ryujinx.exe")]
    [InlineData(@"C:\Program Files\BlueStacks_nxt\HD-Player.exe")]
    public void AnEmulator_IsAGame_EvenInAWindow(string path)
    {
        var verdict = GameClassifier.Classify(Windowed with { ExecutablePath = path });

        Assert.True(verdict.IsGame);
        Assert.True(verdict.Evidence.HasFlag(GameEvidence.KnownEmulator));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Downloads\Bodycam\Bodycam\Binaries\Win64\Bodycam-Win64-Shipping.exe", "UnrealWindow")]
    [InlineData(@"D:\Stuff\PAYDAY3Client-Win64-Shipping.exe", "SomeWindow")]
    [InlineData(@"D:\Stuff\Phasmophobia.exe", "UnityWndClass")]
    [InlineData(@"D:\Stuff\OldGame.exe", "YYGameMakerYY")]
    [InlineData(@"C:\Users\me\AppData\Local\Roblox\Versions\version-5b077c\RobloxPlayerBeta.exe", "WINDOWSCLIENT")]
    public void AProgramBuiltOnAGameEngine_IsAGame_EvenInAWindow(string path, string windowClass)
    {
        var verdict = GameClassifier.Classify(Windowed with { ExecutablePath = path, WindowClass = windowClass });

        Assert.True(verdict.IsGame);
        Assert.True(verdict.Evidence.HasFlag(GameEvidence.GameEngine) || verdict.Evidence.HasFlag(GameEvidence.GameStore));
    }

    [Fact]
    public void TheFilesBesideAProgram_MakeItAGame()
    {
        var path = @"D:\Anywhere\Thing.exe";

        Assert.True(GameClassifier.Classify(Windowed with { ExecutablePath = path, Files = GameFiles.Engine }).Evidence.HasFlag(GameEvidence.GameEngine));
        Assert.True(GameClassifier.Classify(Windowed with { ExecutablePath = path, Files = GameFiles.Store }).IsGame);
        Assert.Equal(GameVerdictKind.NotYet, GameClassifier.Classify(Windowed with { ExecutablePath = path }).Kind);
    }

    [Theory]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\BeamNG.drive\Bin64\BeamNG.drive.x64.exe")]
    [InlineData(@"C:\Program Files\Epic Games\GTAV\PlayGTAV.exe")]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games\Assassin's Creed Mirage\ACMirage.exe")]
    [InlineData(@"C:\XboxGames\Forza Horizon 5\Content\ForzaHorizon5.exe")]
    [InlineData(@"E:\GOG Games\The Witcher 3\bin\x64\witcher3.exe")]
    [InlineData(@"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT.exe")]
    public void AProgramInAGameStoresLibrary_IsAGame_EvenInAWindow(string path)
    {
        var verdict = GameClassifier.Classify(Windowed with { ExecutablePath = path });

        Assert.True(verdict.IsGame);
        Assert.True(verdict.Evidence.HasFlag(GameEvidence.GameStore));
    }

    [Fact]
    public void Minecraft_IsJavaInAGameWindow_AndOtherJavaProgramsAreNot()
    {
        var java = @"C:\Users\me\.lunarclient\jre\zulu17\bin\javaw.exe";

        Assert.True(GameClassifier.Classify(Windowed with { ExecutablePath = java, WindowClass = "GLFW30" }).IsGame);
        Assert.False(GameClassifier.Classify(Windowed with { ExecutablePath = java, WindowClass = "SunAwtFrame" }).IsGame);

        // The same window class under a program that is not Java is a tool as often as a game: it says nothing by itself.
        Assert.False(GameClassifier.Classify(Windowed with { ExecutablePath = @"C:\Tools\viewer.exe", WindowClass = "GLFW30" }).IsGame);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe", "Chrome_WidgetWin_1")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "Qt5QWindowIcon")]
    [InlineData(@"C:\Program Files\mpv\mpv.exe", "mpv")]
    [InlineData(@"C:\Windows\explorer.exe", "Progman")]
    [InlineData(@"C:\Program Files\Microsoft Office\root\Office16\POWERPNT.EXE", "screenClass")]
    [InlineData(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe", "GHOST_WindowClass")]
    [InlineData(@"C:\Program Files\Epic Games\UE_5.4\Engine\Binaries\Win64\UnrealEditor.exe", "UnrealWindow")]
    [InlineData(@"C:\Program Files (x86)\Epic Games\Launcher\Portal\Binaries\Win64\EpicGamesLauncher.exe", "UnrealWindow")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\wallpaper_engine\wallpaper64.exe", "WPEWindow")]
    [InlineData(@"C:\Program Files (x86)\Steam\steam.exe", "SDL_app")]
    [InlineData(@"C:\Program Files\Kairos\Duet Display\duet.exe", "DuetWindow")]
    [InlineData(@"C:\Program Files\obs-studio\bin\64bit\obs64.exe", "Qt6QWindowIcon")]
    public void AProgramThatIsNeverAGame_IsNotOne_HoweverItFillsTheScreenAndTheCard(string path, string windowClass)
    {
        var everything = Borderless(Windowed) with
        {
            ExecutablePath = path, WindowClass = windowClass, ExclusiveFullscreen = true, InWindowsGameList = true,
            Files = GameFiles.Engine | GameFiles.Store, SustainedGraphicsLoad = true,
        };

        var verdict = GameClassifier.Classify(everything);

        Assert.Equal(GameVerdictKind.Never, verdict.Kind);
        Assert.False(verdict.IsGame);
    }

    [Theory]
    [InlineData(@"C:\Program Files\Adobe\Adobe Premiere Pro 2025\Adobe Premiere Pro.exe")]
    [InlineData(@"C:\Program Files\Adobe\Adobe Premiere Pro (Beta)\Adobe Premiere Pro (Beta).exe")]
    [InlineData(@"C:\Program Files\Adobe\Adobe After Effects 2025\Support Files\AfterFX.exe")]
    [InlineData(@"C:\Program Files\Adobe\Adobe Media Encoder 2025\Adobe Media Encoder.exe")]
    [InlineData(@"C:\Program Files\Adobe\Adobe Photoshop 2025\Photoshop.exe")]
    [InlineData(@"C:\Program Files\Adobe\Adobe Lightroom Classic\Lightroom.exe")]
    [InlineData(@"C:\Program Files\Blackmagic Design\DaVinci Resolve\Resolve.exe")]
    [InlineData(@"C:\Program Files\Blender Foundation\Blender 4.2\blender.exe")]
    [InlineData(@"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe")]
    [InlineData(@"C:\Program Files\Epic Games\UE_5.4\Engine\Binaries\Win64\UnrealEditor.exe")]
    [InlineData(@"C:\Program Files\Unity\Hub\Editor\6000.0.23f1\Editor\Unity.exe")]
    [InlineData(@"C:\Program Files\Maxon Cinema 4D 2025\Cinema 4D.exe")]
    [InlineData(@"C:\Program Files\VEGAS\VEGAS Pro 21.0\vegas210.exe")]
    [InlineData(@"C:\Program Files\Nuke15.1v3\Nuke15.1.exe")]
    [InlineData(@"C:\Program Files\Adobe\Adobe Substance 3D Painter\Adobe Substance 3D Painter.exe")]
    public void ACreativeApp_IsNeverAGame_AndIsTreatedAsOneOnlyWhenTheUserAsksForIt(string path)
    {
        // Everything a game would show, and more: by default it still is not one, and it is not even worth another look.
        var busy = Borderless(Windowed) with
        {
            ExecutablePath = path, ExclusiveFullscreen = true, InWindowsGameList = true, Files = GameFiles.Engine | GameFiles.Store, SustainedGraphicsLoad = true,
        };
        Assert.Equal(new GameVerdict(GameVerdictKind.Never, GameEvidence.None), GameClassifier.Classify(busy));
        Assert.Equal(GameVerdictKind.Never, GameClassifier.Classify(busy, games: true, creativeApps: false).Kind);

        // Asked for: it is one by its name alone, idle in a window as much as rendering full screen, with or without games being looked for.
        var idle = Windowed with { ExecutablePath = path };
        Assert.Equal(new GameVerdict(GameVerdictKind.Game, GameEvidence.CreativeApp), GameClassifier.Classify(idle, games: true, creativeApps: true));
        Assert.Equal(new GameVerdict(GameVerdictKind.Game, GameEvidence.CreativeApp), GameClassifier.Classify(busy, games: false, creativeApps: true));
        Assert.True(GameClassifier.IsKnown(GameEvidence.CreativeApp));
    }

    [Theory]
    [InlineData(@"C:\Program Files\Adobe\Adobe Audition 2025\Adobe Audition.exe")] // sound
    [InlineData(@"C:\Program Files\Adobe\Adobe Creative Cloud\ACC\Creative Cloud.exe")] // the launcher
    [InlineData(@"C:\Program Files\Adobe\Acrobat DC\Acrobat\Acrobat.exe")] // reading
    [InlineData(@"C:\Program Files\Aseprite\Aseprite.exe")] // pixels
    [InlineData(@"C:\Program Files\Microsoft VS Code\Code.exe")] // code
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    public void AProgramThatAsksLittleOfTheGraphicsCard_IsNotACreativeApp_WhateverTheUserAskedFor(string path)
    {
        var verdict = GameClassifier.Classify(Borderless(Windowed) with { ExecutablePath = path, SustainedGraphicsLoad = true }, games: true, creativeApps: true);

        Assert.Equal(GameVerdictKind.Never, verdict.Kind);
    }

    [Fact]
    public void WithGamesNotLookedFor_NothingButACreativeAppIsEverAVerdict()
    {
        var store = Windowed with { ExecutablePath = @"C:\Program Files (x86)\Steam\steamapps\common\BeamNG.drive\BeamNG.drive.exe" };
        var emulator = Windowed with { ExecutablePath = @"D:\Program files\Dolphin-x64\Dolphin.exe" };
        var unknown = Borderless(Windowed) with { ExecutablePath = @"C:\Things\thing.exe", SustainedGraphicsLoad = true };

        Assert.All(new[] { store, emulator, unknown }, facts =>
        {
            Assert.True(GameClassifier.Classify(facts).IsGame);
            Assert.Equal(GameVerdictKind.Never, GameClassifier.Classify(facts, games: false, creativeApps: true).Kind);
            Assert.Equal(GameVerdictKind.Never, GameClassifier.Classify(facts, games: false, creativeApps: false).Kind);
        });
    }

    [Fact]
    public void AProgramWindowsListsAsAGame_IsOneOnceItBehavesLikeOne()
    {
        var listed = Windowed with { ExecutablePath = @"C:\Users\me\Downloads\driver.exe", InWindowsGameList = true };

        // In a window and idle it is only worth measuring; filling the screen or keeping the card busy settles it.
        Assert.Equal(GameVerdictKind.Watch, GameClassifier.Classify(listed).Kind);
        Assert.True(GameClassifier.Classify(Borderless(listed)).IsGame);
        Assert.True(GameClassifier.Classify(listed with { SustainedGraphicsLoad = true }).IsGame);
    }

    [Fact]
    public void AProgramInAFolderNamedGames_IsOneOnceItBehavesLikeOne()
    {
        var kept = Windowed with { ExecutablePath = @"D:\Games\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe" };

        Assert.True(GameClassifier.Classify(kept).Evidence.HasFlag(GameEvidence.GamesFolder));
        Assert.Equal(GameVerdictKind.Watch, GameClassifier.Classify(kept).Kind);
        Assert.True(GameClassifier.Classify(Borderless(kept)).IsGame);
    }

    [Fact]
    public void AnUnknownProgram_IsAGame_OnlyWhenItFillsTheScreenAndKeepsTheCardBusy()
    {
        var unknown = Windowed with { ExecutablePath = @"C:\Things\thing.exe" };

        Assert.Equal(GameVerdictKind.NotYet, GameClassifier.Classify(unknown).Kind);

        // A busy card in a window is an editor or a viewer as often as a game.
        Assert.Equal(GameVerdictKind.NotYet, GameClassifier.Classify(unknown with { SustainedGraphicsLoad = true }).Kind);

        // A full screen alone is a film or a slide show as often as a game: it is measured, not believed.
        Assert.Equal(GameVerdictKind.Watch, GameClassifier.Classify(Borderless(unknown)).Kind);

        var verdict = GameClassifier.Classify(Borderless(unknown) with { SustainedGraphicsLoad = true });
        Assert.True(verdict.IsGame);
        Assert.Equal(GameEvidence.Fullscreen | GameEvidence.GraphicsLoad, verdict.Evidence);
    }

    [Fact]
    public void AMaximizedWindow_IsNotFullScreen_UnlessWindowsSaysItIsExclusive()
    {
        var maximized = Windowed with { ExecutablePath = @"C:\Things\thing.exe", CoversMonitor = true, HasCaption = true, SustainedGraphicsLoad = true };

        Assert.False(GameClassifier.Classify(maximized).IsGame);

        var exclusive = GameClassifier.Classify(maximized with { ExclusiveFullscreen = true });
        Assert.True(exclusive.IsGame);
        Assert.True(exclusive.Evidence.HasFlag(GameEvidence.ExclusiveFullscreen));
    }

    [Fact]
    public void ExclusiveFullScreen_SaysNothingForAWindowThatDoesNotCoverTheMonitor()
    {
        // Just after the user switches away from a game Windows may still report it, while another window is already in front.
        var other = Windowed with { ExecutablePath = @"D:\Games\Launcher\thing.exe", ExclusiveFullscreen = true };

        var verdict = GameClassifier.Classify(other);

        Assert.False(verdict.IsGame);
        Assert.False(verdict.Evidence.HasFlag(GameEvidence.Fullscreen));
        Assert.False(verdict.Evidence.HasFlag(GameEvidence.ExclusiveFullscreen));
    }

    [Fact]
    public void AProgramWhosePathWindowsWillNotGive_IsStillFoundByHowItBehaves()
    {
        var hidden = Borderless(Windowed) with { ExecutablePath = "" };

        Assert.Equal(GameVerdictKind.Watch, GameClassifier.Classify(hidden).Kind);
        Assert.True(GameClassifier.Classify(hidden with { SustainedGraphicsLoad = true }).IsGame);
    }

    [Theory]
    [InlineData(40, 0, 0, true)] // drawing
    [InlineData(10, 0, 0, true)] // just enough drawing
    [InlineData(9, 0, 0, false)] // a window's own animation
    [InlineData(60, 10, 0, false)] // drawing a video it is decoding
    [InlineData(2, 0, 2048, true)] // paused in a menu, with its scene still on the card
    [InlineData(2, 0, 300, false)] // an ordinary program's surfaces
    [InlineData(2, 5, 2048, false)] // a large video
    public void AReadingOfTheGraphicsCard_IsAGames_WhenItDrawsOrHoldsMemory_AndDecodesNoVideo(
        double render, double decode, long dedicatedMegabytes, bool expected)
    {
        Assert.Equal(expected, GameClassifier.IsGraphicsLoad(new GpuUsage(render, decode, dedicatedMegabytes << 20)));
    }

    [Theory]
    [InlineData(20, 0, 0, true)] // drawing
    [InlineData(0.5, 2048, 0, true)] // switched away from, with its scene still on the card
    [InlineData(0.5, 0, 900, true)] // the same on a built-in graphics chip, which keeps it in the PC's own memory
    [InlineData(0.5, 40, 60, false)] // a program that filled the screen for a while and is done
    public void AProgramStillUsesTheGraphicsCard_WhileItDrawsOrHoldsMemoryThere(double render, long dedicatedMegabytes, long sharedMegabytes, bool expected)
    {
        Assert.Equal(expected, GameClassifier.IsStillUsingGraphics(new GpuUsage(render, 0, dedicatedMegabytes << 20, sharedMegabytes << 20)));
    }

    [Fact]
    public void OnlyAGameFoundByItsBehaviorAlone_IsAGuess()
    {
        Assert.False(GameClassifier.IsKnown(GameEvidence.Fullscreen | GameEvidence.GraphicsLoad));
        Assert.False(GameClassifier.IsKnown(GameEvidence.Fullscreen | GameEvidence.ExclusiveFullscreen | GameEvidence.GraphicsLoad));
        Assert.True(GameClassifier.IsKnown(GameEvidence.GameStore));
        Assert.True(GameClassifier.IsKnown(GameEvidence.KnownEmulator | GameEvidence.Fullscreen));
        Assert.True(GameClassifier.IsKnown(GameEvidence.WindowsGameList | GameEvidence.Fullscreen));
        Assert.True(GameClassifier.IsKnown(GameEvidence.GamesFolder | GameEvidence.GraphicsLoad));
    }

    [Fact]
    public void TheNameShownForAGame_IsItsWindowsTitle_OrItsProgramsName()
    {
        Assert.Equal("Cyberpunk 2077", new GameFacts { Title = "  Cyberpunk 2077 ", ExecutablePath = @"D:\Games\x\Cyberpunk2077.exe" }.DisplayName);
        Assert.Equal("Cyberpunk2077", new GameFacts { Title = "", ExecutablePath = @"D:\Games\x\Cyberpunk2077.exe" }.DisplayName);
        Assert.Equal(60, new GameFacts { Title = new string('x', 200) }.DisplayName.Length);
    }
}
