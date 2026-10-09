using System.IO;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Assistant.SmokeTests.Support;
using Assistant.UI.Capture;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Capture;
using Assistant.Windows.Hotkeys;
using Assistant.Windows.Placement;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklist 9: the Visual Intelligence shortcut captures the screen, the user picks a part of it, and a question about it is answered from the pixels they
/// picked. The shortcut, controller, permission, conversation, picture preparation and model request are the app's own; the screen is a scripted one and the
/// user's drag is scripted (the real overlay needs a person to drag), so that the check knows exactly which pixels the model must be given.
/// </summary>
public sealed class ScreenshotSmokeTests
{
    private const int WmHotkey = 0x0312;
    private static readonly Hotkey TestChord = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F18");
    // Large enough that the app does not scale it up for the model (a screenshot under 512 x 512 pixels is enlarged), so the pixels can be compared exactly.
    private static readonly ScreenRect Selected = new(100, 50, 700, 570);

    [Fact]
    public Task TheShortcutCapturesTheScreen_AQuestionAboutThePickedPartGoesToTheModelWithThosePixels_AndNothingIsSaved() => Smoke.RunInFolderAsync(async scratch =>
    {
        var screen = new ScriptedScreen();
        var overlay = new ScriptedOverlay(Selected);
        await using var model = FakeLocalModel.Create(
            scratch, new FakeEngineScenario { Chat = new FakeChatReply { Pieces = ["It is a colored pattern."] } }, readsPictures: true);
        await using var app = await SmokeApp.StartAsync(scratch, services =>
        {
            model.Replace(services);
            services.AddSingleton<IScreenCapture>(screen);
            services.AddSingleton<ICaptureOverlay>(overlay);
        });
        await app.ChangeSettingsAsync(model.Use);
        var window = app.Get<AssistantWindow>();
        var conversation = app.Get<ConversationViewModel>();
        window.ShowActivated = false;

        // The shortcut, as the bootstrapper binds it: its own registration with Windows, its own key.
        var hotkeys = app.Services.GetRequiredKeyedService<GlobalHotkeyService>(Assistant.UI.Bootstrap.ServiceCollectionExtensions.VisualIntelligenceHotkey);
        var visual = app.Get<VisualIntelligenceController>();
        using var binding = new OverlayHotkeyBinding(window, hotkeys, TestChord, () => _ = visual.InvokeAsync());
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Assert.True(hotkeys.IsRegistered, "Windows did not accept the chord.");
        PostMessage(handle, WmHotkey, GlobalHotkeyService.VisualIntelligenceHotkeyId, (0x81 << 16) | 0x7);

        // Every monitor was captured, the overlay was shown over them, and the part the user picked is in the conversation as a chip, waiting for a question.
        await Wait.UntilAsync(() => conversation.Attachments.Count == 1 && window.IsVisible, "The shortcut did not open the conversation with the picture.");
        Assert.Equal(1, screen.Captures);
        Assert.Equal(1, overlay.Shown);
        var picture = conversation.Attachments[0];
        Assert.True(picture.IsCapture);
        Assert.Equal((600, 520), (picture.PixelWidth, picture.PixelHeight));
        Assert.Empty(conversation.Messages);

        Assert.True(conversation.Ask("What is in this picture?"));
        await Wait.UntilAsync(
            () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete,
            () => "The question was not answered: " + string.Join(" | ", conversation.Messages.Select(message => message.Status + ": " + message.Text)));
        Assert.Equal("It is a colored pattern.", conversation.Messages[1].Text);

        // The model was given the question and a PNG of exactly the picked pixels, and nothing else of the screen.
        using var request = JsonDocument.Parse(model.RequestBody(1));
        var content = request.RootElement.GetProperty("messages").EnumerateArray()
            .Where(message => message.GetProperty("role").GetString() == "user")
            .SelectMany(message => message.GetProperty("content").EnumerateArray()).ToArray();
        Assert.Contains(content, part => part.TryGetProperty("text", out var text) && text.GetString()!.Contains("What is in this picture?", StringComparison.Ordinal));
        var url = Assert.Single(content, part => part.TryGetProperty("image_url", out _)).GetProperty("image_url").GetProperty("url").GetString()!;
        Assert.StartsWith("data:image/png;base64,", url, StringComparison.Ordinal);
        var decoded = new PngBitmapDecoder(
            new MemoryStream(Convert.FromBase64String(url["data:image/png;base64,".Length..])), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal((600, 520), (decoded.PixelWidth, decoded.PixelHeight));
        var pixels = new byte[600 * 520 * 4];
        new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0).CopyPixels(pixels, 600 * 4, 0);
        Assert.Equal(ScriptedScreen.PixelsOf(Selected), pixels);

        // Nothing of the screen was written anywhere: no picture file in the data folder or beside it, and nothing about it in the logs.
        var written = Directory.EnumerateFiles(scratch.Path, "*", SearchOption.AllDirectories)
            .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp").ToArray();
        Assert.Empty(written);
        Assert.DoesNotContain(app.Logs.Lines, line => line.Contains("colored pattern", StringComparison.OrdinalIgnoreCase));

        await AppWindows.CloseAsync(window);
    });

    [Fact]
    public Task WithScreenCaptureTurnedOff_NothingIsCaptured_AndTheConversationSaysSo() => Smoke.RunAsync(async app =>
    {
        var screen = new ScriptedScreen();
        await app.ChangeSettingsAsync(settings => settings with { Permissions = settings.Permissions with { ScreenCapture = false } });
        var window = app.Get<AssistantWindow>();
        window.ShowActivated = false;
        var conversation = app.Get<ConversationViewModel>();

        await app.Get<VisualIntelligenceController>().InvokeAsync();

        Assert.Equal(0, screen.Captures);
        await Wait.UntilAsync(() => conversation.Messages.Count == 1, "The conversation did not say why.");
        Assert.Equal(VisualIntelligenceController.TurnedOffText, conversation.Messages[0].Text);
        await AppWindows.CloseAsync(window);
    }, services => services.AddSingleton<IScreenCapture>(new ScriptedScreen()));

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);

    /// <summary>One monitor of 800 by 600 pixels whose every pixel says where it is, so that a crop can be checked pixel for pixel.</summary>
    private sealed class ScriptedScreen : IScreenCapture
    {
        private static readonly CaptureMonitor Monitor = new(0, new ScreenRect(0, 0, 800, 600), 96, true);

        public int Captures { get; private set; }

        public static byte[] PixelsOf(ScreenRect region)
        {
            var pixels = new byte[region.Width * region.Height * 4];
            for (var y = 0; y < region.Height; y++)
            {
                for (var x = 0; x < region.Width; x++)
                {
                    BitConverter.TryWriteBytes(pixels.AsSpan((y * region.Width + x) * 4), Coded(region.Left + x, region.Top + y));
                }
            }

            return pixels;
        }

        // Opaque, and telling its position (a smooth pattern, so that the picture stays a small PNG).
        private static uint Coded(int x, int y) => 0xFF000000u | (uint)((x & 0xFF) << 16) | (uint)((y & 0xFF) << 8) | 0x80u;

        public IReadOnlyList<CaptureMonitor> GetMonitors() => [Monitor];

        public Task<IReadOnlyList<CapturedImage>> CaptureAllMonitorsAsync(CancellationToken cancellationToken = default)
        {
            Captures++;
            IReadOnlyList<CapturedImage> all =
                [new CapturedImage(CaptureKind.Monitor, CaptureMethod.ScreenCopy, Monitor.Bounds, Monitor, PixelsOf(Monitor.Bounds), DateTimeOffset.UtcNow)];
            return Task.FromResult(all);
        }

        public Task<CapturedImage> CaptureMonitorAsync(CaptureMonitor monitor, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CapturedImage> CaptureWindowAsync(nint window, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<CapturedImage> CaptureRegionAsync(ScreenRect region, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>The user's drag: picks a part of the first snapshot and chooses Ask.</summary>
    private sealed class ScriptedOverlay(ScreenRect selected) : ICaptureOverlay
    {
        public int Shown { get; private set; }

        public Task<CaptureOutcome?> SelectAsync(IReadOnlyList<CapturedImage> snapshots, ChipAvailability imageSearch, CancellationToken cancellationToken)
        {
            Shown++;
            return Task.FromResult<CaptureOutcome?>(new CaptureOutcome(CaptureAction.Ask, snapshots[0].Crop(selected), ""));
        }
    }
}
