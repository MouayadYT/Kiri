using System.Runtime.InteropServices;
using System.Windows.Interop;
using Assistant.Core.Settings;
using Assistant.SmokeTests.Support;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.UI.Windowing;
using Assistant.Windows.Hotkeys;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklists 2 and 3: the shortcut opens the Assistant, and the one window moves between its forms: the compact bar, the floating conversation the question
/// grows into, and the full History window the conversation can be opened in. The windows are the app's own, built by its container, on a real desktop.
/// </summary>
public sealed class SurfacesSmokeTests
{
    private const int WmHotkey = 0x0312;

    // An uncommon chord, so that a running Assistant (which holds Alt+A) never decides whether this check passes.
    private static readonly Hotkey TestChord = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F19");

    [Fact]
    public Task TheDocumentedShortcutsAreValidAndDistinct_AndWindowsAcceptsAChordForTheBar() => Smoke.RunAsync(app =>
    {
        // What a first run registers (PROJECT_SPEC §4.0): four chords, each a key Windows can be given, no two alike.
        var shortcuts = new AppSettings().Hotkeys;
        Hotkey?[] all = [shortcuts.SearchOrAsk, shortcuts.SelectedTextActions, shortcuts.SelectedTextByCopy, shortcuts.VisualIntelligence];
        Assert.All(all, chord => Assert.True(chord is not null && HotkeyKey.TryParse(chord.Key, out _)));
        Assert.Equal(4, all.Distinct().Count());
        Assert.Equal("Alt+A", shortcuts.SearchOrAsk!.ToString());

        // A real registration with Windows, through the window's handle, and its release.
        var window = app.Get<AssistantWindow>();
        var hotkeys = app.Get<GlobalHotkeyService>();
        var handle = new WindowInteropHelper(window).EnsureHandle();
        using (var binding = new OverlayHotkeyBinding(window, hotkeys, TestChord))
        {
            Assert.True(hotkeys.IsRegistered, "Windows did not accept the chord.");
        }

        Assert.False(hotkeys.IsRegistered);
        Assert.NotEqual(0, handle);
        window.Close();
        return Task.CompletedTask;
    });

    [Fact]
    public Task TheShortcutOpensTheBar_AnAskedQuestionGrowsIntoTheConversation_AndItOpensInTheHistoryWindow() => Smoke.RunAsync(async app =>
    {
        var window = app.Get<AssistantWindow>();
        var controller = app.Get<AssistantWindowStateController>();
        var conversation = app.Get<ConversationViewModel>();
        var history = app.Get<HistoryViewModel>();

        // As the bootstrapper does: the controller that opens the History window is built, and the shortcut is bound to the bar.
        app.Get<HistoryWindowController>();
        window.ShowActivated = false;
        var hotkeys = app.Get<GlobalHotkeyService>();
        using var binding = new OverlayHotkeyBinding(window, hotkeys, TestChord, controller.Invoke);
        var handle = new WindowInteropHelper(window).EnsureHandle();
        Assert.True(hotkeys.IsRegistered, "Windows did not accept the chord.");
        Assert.False(window.IsVisible);

        // The chord: Windows sends this message to the window that registered it. The registration is real; the message is posted by hand,
        // so the check needs no input access.
        void PressChord() => PostMessage(handle, WmHotkey, GlobalHotkeyService.HotkeyId, (0x82 << 16) | 0x7);

        // Compact: the bar.
        PressChord();
        await Wait.UntilAsync(() => window.IsVisible, "The shortcut did not open the Assistant.");
        Assert.Equal(AssistantWindowState.Compact, window.State);

        // Floating: asking from the bar grows the same window into the conversation, with the question as typed.
        var bar = app.Get<SearchOrAskViewModel>();
        bar.Query = "9 + 10";
        Assert.True(bar.AskCommand.CanExecute(null));
        bar.AskCommand.Execute(null);
        await Wait.UntilAsync(
            () => window.State == AssistantWindowState.FloatingConversation && !window.IsExpanding, "The bar did not grow into the conversation.");
        Assert.True(window.IsVisible);
        Assert.Equal("9 + 10", conversation.Messages[0].Text);
        await Wait.UntilAsync(() => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete, "The sum was not answered.");

        // The shortcut while the conversation is up brings that same window and conversation forward; it does not start the bar over.
        PressChord();
        await Task.Delay(300);
        Assert.Equal(AssistantWindowState.FloatingConversation, window.State);
        Assert.True(window.IsVisible);
        Assert.Equal("9 + 10", conversation.Messages[0].Text);

        // Full: the conversation moves to the History window, which has it open, and the floating panel goes.
        conversation.OpenInHistoryCommand.Execute(null);
        var historyWindow = app.Get<HistoryWindow>();
        await Wait.UntilAsync(() => historyWindow.IsVisible, "The History window did not open.");
        await Wait.UntilAsync(() => !window.IsVisible, "The floating panel stayed beside the History window.");
        Assert.Equal(conversation.Id, history.Selected?.Id);
        Assert.Equal("9 + 10", history.Selected!.Title);

        historyWindow.CloseForGood();
        await AppWindows.CloseAsync(window);
    });

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
