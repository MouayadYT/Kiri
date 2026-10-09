using System.Windows;
using System.Windows.Interop;
using Assistant.Core.Settings;
using Assistant.Core.Startup;
using Assistant.UI.Windowing;
using Assistant.Windows.Hotkeys;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

// The Assistant's window when it starts with Windows, the tray menu's New Conversation, and the sign-in switch on the General page
// (PROJECT_SPEC §4.9, step 121).
public sealed partial class PromptInputControlTests
{
    [Fact]
    public void AStartWithWindowsMakesTheWindowButShowsNothing_AndTheShortcutStillOpensTheBar() => RunSta(() =>
    {
        // What AppBootstrapper does when LaunchOptions.StartHidden: the bindings exist, the handle is made, nothing is shown.
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        var assistant = CreateAssistant();
        var window = assistant.Window;
        window.ShowActivated = false;
        var shortcut = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F21");
        using var hotkeys = new GlobalHotkeyService(NullLogger<GlobalHotkeyService>.Instance);
        using var binding = new OverlayHotkeyBinding(window, hotkeys, shortcut, assistant.Controller.Invoke);
        try
        {
            Assert.True(LaunchOptions.Parse(["--background"]).StartHidden);
            Assert.False(hotkeys.IsRegistered);

            var handle = new WindowInteropHelper(window).EnsureHandle();

            Assert.True(hotkeys.IsRegistered, "The shortcut is not registered with Windows while the window is hidden.");
            Assert.False(window.IsVisible);
            Assert.Equal(0, ShownAssistants());

            var invocations = 0;
            hotkeys.Invoked += (_, _) => invocations++;
            PostMessage(handle, 0x0312 /* WM_HOTKEY */, 0x5341, (0x84 /* F21 */ << 16) | 0x7);
            WaitUntil(() => invocations == 1 && window.IsVisible);
            Assert.Equal(AssistantWindowState.Compact, window.State);
            Assert.Equal(handle, new WindowInteropHelper(window).Handle);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    [Fact]
    public void NewConversationOpensTheEmptyConversationWhateverTheWindowWasShowing() => RunSta(() =>
    {
        var (controller, window, bar, conversation, _, _) = CreateController();
        bar.Query = "an unfinished search";
        conversation.StartNew("What is on my calendar?");
        var before = conversation.Id;

        controller.OpenNewConversation();

        // The panel opens (or the bar grows into it) on a conversation with nothing in it and nothing attached, ready for the first message.
        Assert.Equal([null], window.ConversationsShown);
        Assert.Equal(AssistantWindowState.FloatingConversation, controller.State);
        Assert.Empty(conversation.Messages);
        Assert.Empty(conversation.Chips);
        Assert.Equal("", conversation.Draft);
        Assert.NotEqual(before, conversation.Id);

        // What was typed in the bar is not part of it: it is still there for the next time the bar opens.
        Assert.Equal("an unfinished search", bar.Query);
    });

    [Fact]
    public void TheEmptyConversationHasAComposerToBeginIn_AndWhatIsTypedStartsTheConversation() => RunSta(() =>
    {
        var (controller, _, _, conversation, _, _) = CreateController();

        controller.OpenNewConversation();

        // Without a message, an attachment or a notice there would be nothing to type in: the one begun on purpose has the composer.
        Assert.True(conversation.CanCompose);
        Assert.Equal(Assistant.UI.ViewModels.ConversationViewModel.BlankComposerPlaceholder, conversation.ComposerPlaceholder);
        Assert.False(conversation.AskCommand.CanExecute(null));

        conversation.Draft = "What is on my calendar?";
        Assert.True(conversation.AskCommand.CanExecute(null));
        conversation.AskCommand.Execute(null);

        var asked = Assert.Single(conversation.Messages);
        Assert.Equal("What is on my calendar?", asked.Text);
        Assert.Equal("", conversation.Draft);
    });

    [Fact]
    public void AConversationThatIsNotBeganEmptyKeepsTheComposerItAlwaysHad() => RunSta(() =>
    {
        var (_, _, _, conversation, _, _) = CreateController();

        // Any other beginning forgets that the last one was empty on purpose: with nothing in it there is still no composer.
        conversation.StartEmpty();
        Assert.True(conversation.CanCompose);
        conversation.StartWithFiles([], [], null);
        Assert.False(conversation.CanCompose);
        Assert.Equal("Ask a follow-up", conversation.ComposerPlaceholder);

        conversation.StartEmpty();
        conversation.StartNew("A question");
        Assert.Equal("Ask a follow-up", conversation.ComposerPlaceholder);
    });

    [Fact]
    public void NewConversationOpensWhereTheUserDraggedTheWindow() => RunSta(() =>
    {
        var (controller, window, _, _, _, _) = CreateController();
        var dropped = new Assistant.Windows.Placement.ScreenPoint(-700, 300);
        window.SurfaceTop = dropped;
        window.RaiseMoved();

        controller.OpenNewConversation();

        Assert.Equal([dropped], window.ConversationsShown);
    });

    [Fact]
    public void TheSignInSwitchAddsAndRemovesTheEntry_AndIsSavedOnlyWhenThatWorked() => RunSta(() =>
    {
        var launch = new FakeLaunchAtLogin();
        var kit = CreateSettingsKit(launchAtLogin: launch);
        var page = kit.Model.General;

        page.StartAtSignIn = true;
        kit.Settle();
        Assert.Equal(1, launch.Enabled);
        Assert.True(kit.Saved.LaunchAtLogin.Enabled);
        Assert.False(kit.Model.HasNotice);

        page.StartAtSignIn = false;
        kit.Settle();
        Assert.Equal(1, launch.Disabled);
        Assert.False(kit.Saved.LaunchAtLogin.Enabled);

        // An entry that cannot be written: the switch goes back, nothing is saved, and the window says so.
        launch.Works = false;
        page.StartAtSignIn = true;
        kit.Settle();
        Assert.False(page.StartAtSignIn);
        Assert.False(kit.Saved.LaunchAtLogin.Enabled);
        Assert.Equal("The Assistant couldn't be set to start when you sign in.", kit.Model.Notice);

        // Showing the saved settings never changes the entry.
        var shown = CreateSettingsKit(new AppSettings { LaunchAtLogin = new LaunchAtLoginSettings { Enabled = true } }, launchAtLogin: launch);
        Assert.True(shown.Model.General.StartAtSignIn);
        Assert.Equal(2, launch.Enabled);
        Assert.Equal(1, launch.Disabled);
    });

    [Fact]
    public void TheGeneralPageSaysWhenWindowsHasTheEntryTurnedOff_AndOnlyThen() => RunSta(() =>
    {
        var launch = new FakeLaunchAtLogin { State = LaunchAtLoginState.TurnedOffInWindows };
        var on = new AppSettings { LaunchAtLogin = new LaunchAtLoginSettings { Enabled = true } };

        var blocked = CreateSettingsKit(on, launchAtLogin: launch).Model.General;
        Assert.True(blocked.HasStartAtSignInNote);
        Assert.Contains("Settings > Apps > Startup", blocked.StartAtSignInNote, StringComparison.Ordinal);

        launch.State = LaunchAtLoginState.On;
        Assert.False(CreateSettingsKit(on, launchAtLogin: launch).Model.General.HasStartAtSignInNote);

        // The switch is off: whatever Windows holds about an old entry is nothing to talk about.
        launch.State = LaunchAtLoginState.TurnedOffInWindows;
        Assert.False(CreateSettingsKit(new AppSettings(), launchAtLogin: launch).Model.General.HasStartAtSignInNote);
    });

    [Fact]
    public void WithoutAnEntryServiceTheSwitchOnlySavesTheSetting() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        kit.Model.General.StartAtSignIn = true;
        kit.Settle();

        Assert.True(kit.Saved.LaunchAtLogin.Enabled);
        Assert.False(kit.Model.General.HasStartAtSignInNote);
    });

    private sealed class FakeLaunchAtLogin : ILaunchAtLogin
    {
        public bool Works { get; set; } = true;

        public LaunchAtLoginState State { get; set; }

        public int Enabled { get; private set; }

        public int Disabled { get; private set; }

        public int Refreshed { get; private set; }

        public LaunchAtLoginState GetState() => State;

        public Task<bool> EnableAsync(CancellationToken cancellationToken = default)
        {
            Enabled++;
            return Task.FromResult(Works);
        }

        public Task<bool> RefreshAsync(CancellationToken cancellationToken = default)
        {
            Refreshed++;
            return Task.FromResult(Works);
        }

        public Task<bool> DisableAsync(CancellationToken cancellationToken = default)
        {
            Disabled++;
            return Task.FromResult(Works);
        }
    }
}
