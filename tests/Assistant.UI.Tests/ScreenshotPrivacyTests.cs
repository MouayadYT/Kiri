using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assistant.Core.Storage;
using Assistant.Core.Domain;
using Assistant.UI.Capture;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.Windows.Capture;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Screenshots do not outlive their use (PROJECT_SPEC §3.5, P7; step 80) ----------------------------------------------------

    private static readonly TimeSpan JustUnderTheLifetime = ScreenAttachments.IdleLifetime - TimeSpan.FromMinutes(1);

    [Fact]
    public void ReleasingACapture_WipesItsBytes_NotJustTheReference() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        Assert.True(MemoryMarshal.TryGetArray(chat.Capture.Data, out var segment));
        var bytes = segment.Array!;
        Assert.Contains(bytes, value => value != 0);

        chat.Screens.Release(chat.Conversation.Id, chat.Capture);

        Assert.All(bytes, value => Assert.Equal(0, value));
        Assert.True(chat.Capture.Data.IsEmpty);
    });

    [Fact]
    public void ACaptureNobodyAsksAbout_IsLetGoOfAfterTheIdleLifetime_Everywhere() => RunSta(() =>
    {
        var clock = new MovableClock(Now);
        var chat = CreateScreenConversation(time: clock);

        clock.Now += JustUnderTheLifetime;
        Assert.Equal(0, chat.Screens.ReleaseIdle());
        Assert.False(chat.Capture.Data.IsEmpty);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, chat.Screens.ReleaseIdle());

        Assert.True(chat.Capture.Data.IsEmpty);
        Assert.Empty(chat.Conversation.Chips);
        Assert.Empty(chat.Contexts.PendingItems(chat.Conversation.Id));
        Assert.False(chat.Provider.HasScreenContext(chat.Conversation.Id));
        Assert.Equal(0, chat.Screens.ReleaseIdle());
    });

    [Fact]
    public void AskingAboutACapture_StartsItsLifetimeAgain() => RunSta(() =>
    {
        var clock = new MovableClock(Now);
        var chat = CreateScreenConversation(time: clock);
        clock.Now += JustUnderTheLifetime;

        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The answer did not end.");

        // It was asked about a moment ago: what has passed since it was taken no longer counts.
        clock.Now += JustUnderTheLifetime;
        Assert.Equal(0, chat.Screens.ReleaseIdle());
        Assert.False(chat.Capture.Data.IsEmpty);

        clock.Now += TimeSpan.FromMinutes(1);
        Assert.Equal(1, chat.Screens.ReleaseIdle());
        Assert.True(chat.Capture.Data.IsEmpty);
    });

    [Fact]
    public void ACaptureTheHistoryWindowHolds_IsLetGoOfWhenItIsIdle_Too() => RunSta(() =>
    {
        var clock = new MovableClock(Now);
        var chat = CreateScreenConversation(time: clock);
        chat.Conversation.Ask("explain this error");
        WaitUntil(() => chat.Model.Requests.Count == 1 && !chat.Conversation.IsAnswering, "The first answer did not end.");
        var history = new HistoryViewModel(new FixedClock(Now), answers: chat.Provider, screens: chat.Screens);
        var opened = history.Open(chat.Conversation.Id, chat.Conversation.Messages, chat.Conversation.UpdatedAt, chat.Conversation.Captures);
        WaitUntil(() => opened.IsLoaded, "The conversation did not load.");

        // It was asked about, so it has no chip; History holds it all the same, for the next question.
        Assert.Empty(history.Chips);
        Assert.False(chat.Capture.Data.IsEmpty);

        clock.Now += ScreenAttachments.IdleLifetime;
        Assert.Equal(1, chat.Screens.ReleaseIdle());

        Assert.Empty(history.Chips);
        Assert.True(chat.Capture.Data.IsEmpty);
        Assert.Empty(chat.Contexts.PendingItems(chat.Conversation.Id));
    });

    [Fact]
    public void WhenTheOverlayFailsBeforeItClosesTheSnapshots_TheControllerWipesThemItself() => RunSta(() =>
    {
        var setup = CreateVisual();
        setup.Overlay.Throws = new InvalidOperationException("the overlay broke");

        var running = setup.Controller.InvokeAsync();
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal(2, setup.Capture.Taken.Count);
        Assert.All(setup.Capture.Taken, snapshot => Assert.Throws<ObjectDisposedException>(() => snapshot.Pixels));
        Assert.False(setup.Controller.IsActive);
    });

    [Fact]
    public void WhenImageSearchCannotSayWhetherItIsAvailable_TheSnapshotsAreStillWiped() => RunSta(() =>
    {
        var setup = CreateVisual(imageSearchOf: _ => throw new InvalidOperationException("the search state was not readable"));

        var running = setup.Controller.InvokeAsync();
        WaitUntil(() => running.IsCompleted, "Visual Intelligence did not end.");

        Assert.Equal(2, setup.Capture.Taken.Count);
        Assert.All(setup.Capture.Taken, snapshot => Assert.Throws<ObjectDisposedException>(() => snapshot.Pixels));
        Assert.Empty(setup.Overlay.Snapshots);
    });

    [Fact]
    public void WhenTheOverlayCloses_ItsWindowsNoLongerHoldAPictureOfTheMonitor() => RunSta(() => WithTheme(() =>
    {
        var first = CodedSnapshot(600, 400, index: 0);
        var second = CodedSnapshot(600, 400, left: 600, index: 1);
        var (overlay, shown) = OffScreenOverlay();
        var task = overlay.SelectAsync([first, second], ChipAvailability.ImageSearchUnavailable, CancellationToken.None);
        Assert.All(shown, window => Assert.NotNull(window.SelectionSurface.Snapshot));

        PressOverlayKey(shown[0].SelectionSurface, Key.Escape, preview: true, source: shown[0]);
        Pump();

        Assert.True(task.IsCompleted);
        Assert.All(shown, window => Assert.Null(window.SelectionSurface.Snapshot));
    }));

    [Fact]
    public void ACopiedPicture_IsMarkedSoThatWindowsKeepsItOffTheCloudClipboard() => RunSta(() =>
    {
        var picture = BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgr32, null, new byte[16], 8);
        picture.Freeze();

        var data = WpfImageClipboard.CreateData(picture);

        Assert.True(data.GetDataPresent(DataFormats.Bitmap));
        var mark = Assert.IsAssignableFrom<Stream>(data.GetData(WpfImageClipboard.CloudUploadFormat));
        var bytes = new byte[4];
        Assert.Equal(4, mark.Read(bytes));
        Assert.Equal(0, BitConverter.ToInt32(bytes));
    });

    [Fact]
    public void WhatTheMessagesSaveOfAGalleryOfCaptures_IsNothing() => RunSta(() =>
    {
        var chat = CreateScreenConversation();
        var mapper = new Assistant.UI.History.MessageMapper(new FakeClipboard());
        var answer = new MessageViewModel(MessageRole.Assistant) { CreatedAt = Now };
        answer.Content.Add(new ImageCollection([chat.Capture]));

        var saved = mapper.ToDomain(answer);

        Assert.Empty(saved.Cards);
        Assert.Empty(saved.ContextItems);
    });

    // ---- The folder for temporary captures, and the lifetime that empties it -----------------------------------------------------

    private static AppPaths TemporaryPaths()
    {
        var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "assistant-captures-" + Guid.NewGuid().ToString("N")));
        paths.EnsureDirectoriesExist();
        return paths;
    }

    [Fact]
    public void TheApplicationEmptiesTheTemporaryCapturesFolder_WhenItStarts_AndWhenItStops() => RunSta(() =>
    {
        var paths = TemporaryPaths();
        try
        {
            var chat = CreateScreenConversation();
            using var lifetime = new CaptureLifetime(
                chat.Screens, new TemporaryCaptureCleaner(paths), action => action(), NullLogger<CaptureLifetime>.Instance);

            // What a run that ended badly left behind.
            File.WriteAllBytes(Path.Combine(paths.TemporaryCapturesDirectory, "left-behind.png"), [1, 2, 3]);
            lifetime.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Empty(Directory.GetFileSystemEntries(paths.TemporaryCapturesDirectory));

            File.WriteAllBytes(Path.Combine(paths.TemporaryCapturesDirectory, "in-use-when-it-stopped.png"), [1, 2, 3]);
            lifetime.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            Assert.Empty(Directory.GetFileSystemEntries(paths.TemporaryCapturesDirectory));
        }
        finally
        {
            Directory.Delete(paths.RootDirectory, recursive: true);
        }
    });

    [Fact]
    public void WhileTheApplicationRuns_IdleCapturesAreLookedForOnTheUiThread() => RunSta(() =>
    {
        var paths = TemporaryPaths();
        try
        {
            var clock = new MovableClock(Now);
            var chat = CreateScreenConversation(time: clock);
            var dispatcher = Dispatcher.CurrentDispatcher;
            var threads = new List<int>();
            using var lifetime = new CaptureLifetime(
                chat.Screens,
                new TemporaryCaptureCleaner(paths),
                action => dispatcher.BeginInvoke(() =>
                {
                    threads.Add(Environment.CurrentManagedThreadId);
                    action();
                }),
                NullLogger<CaptureLifetime>.Instance,
                TimeSpan.FromMilliseconds(20));
            lifetime.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            // Not idle yet: the checks find nothing to let go of.
            WaitUntil(() => threads.Count >= 2, "The captures were not looked at.");
            Assert.False(chat.Capture.Data.IsEmpty);

            clock.Now += ScreenAttachments.IdleLifetime;
            WaitUntil(() => chat.Capture.Data.IsEmpty, "The idle capture was not let go of.");

            Assert.All(threads, thread => Assert.Equal(Environment.CurrentManagedThreadId, thread));
            Assert.Empty(chat.Conversation.Chips);
            lifetime.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            Directory.Delete(paths.RootDirectory, recursive: true);
        }
    });

    // ---- Nothing that handles a screenshot writes to disk -----------------------------------------------------------------------

    // The folders whose code touches a capture: the pixels, the picture the model is given, what OCR reads, the tool that reads the
    // screen, and what an image search sends. Reading a file is fine (an image the user attached); writing one is not (§3.5, P7).
    private static readonly string[] CaptureFolders =
    [
        @"src\Assistant.Windows\Capture",
        @"src\Assistant.Windows\Imaging",
        @"src\Assistant.Windows\Ocr",
        @"src\Assistant.UI\Capture",
        @"src\Assistant.Core\Imaging",
        @"src\Assistant.Core\Ocr",
        @"src\Assistant.Core\ImageSearch",
        @"src\Assistant.Tools\Screen",
    ];

    private static readonly Regex DiskWrite = new(
        @"\bFile\.(Write|Create|Open|Append|Copy|Move|Replace)|\bFileStream\b|\bStreamWriter\b|\bGetTempPath\b|\bGetTempFileName\b"
        + @"|\bCreateTempSubdirectory\b|\bDirectory\.Create|\bTemporaryCapturesDirectory\b|\.Save\(|\bSaveAsync\b|\bSaveAs\b",
        RegexOptions.Compiled);

    // This file is two folders below the repository's root; the tests may run from a build folder elsewhere.
    private static string RepositoryRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));

    [Fact]
    public void NothingThatHandlesAScreenshot_WritesAFile()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var folder in CaptureFolders)
        {
            var path = Path.Combine(root, folder);
            Assert.True(Directory.Exists(path), $"{folder} is not where the check looks for it.");
            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                scanned++;
                var lines = File.ReadAllLines(file);
                for (var index = 0; index < lines.Length; index++)
                {
                    // Comments say what is not done; only code counts.
                    var code = lines[index].Split("//")[0];
                    if (DiskWrite.IsMatch(code) && !code.TrimStart().StartsWith('*'))
                    {
                        offenders.Add($"{Path.GetRelativePath(root, file)}:{index + 1}");
                    }
                }
            }
        }

        Assert.True(scanned > 20, "The check found hardly any code to look at.");
        Assert.Empty(offenders);
    }

    [Fact]
    public void TheCheckForDiskWrites_SeesTheWaysACaptureCouldBeSaved()
    {
        string[] writes =
        [
            "File.WriteAllBytes(path, png);",
            "using var stream = new FileStream(path, FileMode.Create);",
            "var path = Path.GetTempFileName();",
            "encoder.Save(stream);",
            "var folder = paths.TemporaryCapturesDirectory;",
        ];
        Assert.All(writes, line => Assert.Matches(DiskWrite, line));
        Assert.DoesNotMatch(DiskWrite, "var bytes = File.ReadAllBytes(path);");
    }
}
