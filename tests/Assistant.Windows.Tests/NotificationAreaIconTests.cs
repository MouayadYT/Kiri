using Assistant.Windows.Interop;
using Assistant.Windows.Tray;
using Xunit;

namespace Assistant.Windows.Tests;

/// <summary>
/// The Assistant's icon in the notification area (PROJECT_SPEC §4.9, step 121), with the real shell: the icon is added and removed, Windows'
/// messages about it are sent to its window by hand, and the native menu is replaced by a stand-in that answers with a chosen line (the real
/// menu is modal). The tests are synchronous, so the window's thread is the one that sends the messages.
/// </summary>
public sealed class NotificationAreaIconTests
{
    private const uint CallbackMessage = User32.WM_APP + 1;

    // What Windows sends with version 4: the event in the low word, the icon's id in the high word, and the click point in wParam.
    private static void Notify(NotificationAreaIcon icon, uint notification, short x = 0, short y = 0) =>
        User32.SendMessage(
            icon.Window, CallbackMessage, (nuint)((ushort)x | ((uint)(ushort)y << 16)), (nint)(notification | (1u << 16)));

    private static TrayMenuItem[] SampleMenu() =>
    [
        new TrayMenuItem(1, "Open") { IsDefault = true },
        TrayMenuItem.Separator,
        new TrayMenuItem(2, "Exit"),
    ];

    [Fact]
    public void ShowingPutsTheIconInTheNotificationArea_AndHidingTakesItOut()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        Assert.False(icon.IsShown);

        Assert.True(icon.Show());
        Assert.True(icon.IsShown);
        Assert.True(icon.IsInNotificationArea());
        Assert.True(icon.Show());

        icon.Hide();
        Assert.False(icon.IsShown);
        Assert.False(icon.IsInNotificationArea());

        Assert.True(icon.Show());
        Assert.True(icon.IsInNotificationArea());
    }

    [Fact]
    public void TheIconOfTheAppIsLoadedFromItsFile_AndAMissingFileFallsBackToTheSystemIcon()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Assistant.ExplorerExtension", "Assets", "Assistant.ico");
        if (!File.Exists(file))
        {
            // The repository layout is not there (a copy of the tests): the fallback is what is left to check.
            file = Path.Combine(Path.GetTempPath(), "no-such-icon.ico");
        }

        using var fromFile = new NotificationAreaIcon("Assistant", file);
        using var fallback = new NotificationAreaIcon("Assistant", Path.Combine(Path.GetTempPath(), "no-such-icon-" + Guid.NewGuid().ToString("N") + ".ico"));

        Assert.True(fromFile.Show());
        Assert.True(fallback.Show());
    }

    [Fact]
    public void ADisposedIconIsGoneFromTheNotificationArea_AndCannotBeShownAgain()
    {
        var icon = new NotificationAreaIcon("Assistant");
        Assert.True(icon.Show());

        icon.Dispose();
        icon.Dispose();

        Assert.False(icon.IsShown);
        Assert.Throws<ObjectDisposedException>(() => icon.Show());
    }

    [Fact]
    public void AClickOnTheIconAndTheKeyboardOnItAreSelections()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();
        var selections = 0;
        icon.Selected += (_, _) => selections++;

        Notify(icon, Shell32.NIN_SELECT);
        Notify(icon, Shell32.NIN_KEYSELECT);

        Assert.Equal(2, selections);
    }

    [Fact]
    public void TheRawMouseMessagesAreIgnored_SoOneClickIsOneSelection()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();
        var selections = 0;
        icon.Selected += (_, _) => selections++;
        var menusOpened = 0;
        icon.Menu = SampleMenu();
        icon.MenuPresenter = (_, _, _, _) =>
        {
            menusOpened++;
            return 0;
        };

        // WM_LBUTTONDOWN, WM_LBUTTONUP, WM_LBUTTONDBLCLK, WM_RBUTTONUP: with version 4 the selection and the context menu come as their own events.
        foreach (var message in new uint[] { 0x0201, 0x0202, 0x0203, 0x0205 })
        {
            Notify(icon, message);
        }

        Assert.Equal(0, selections);
        Assert.Equal(0, menusOpened);
    }

    [Fact]
    public void TheContextMenuOpensAtTheClickWithTheLinesSet_AndTheChosenLineIsReported()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();
        icon.Menu = SampleMenu();
        var chosen = new List<int>();
        icon.CommandInvoked += (_, id) => chosen.Add(id);
        (nint Owner, int Count, int X, int Y) seen = default;
        icon.MenuPresenter = (owner, items, x, y) =>
        {
            seen = (owner, items.Count, x, y);
            return 2;
        };

        // A click on a taskbar left of and above the main monitor has negative coordinates.
        Notify(icon, User32.WM_CONTEXTMENU, -1900, -35);

        Assert.Equal((icon.Window, 3, -1900, -35), seen);
        Assert.Equal([2], chosen);
    }

    [Fact]
    public void ADismissedMenuReportsNothing_AndAnEmptyMenuDoesNotOpen()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();
        var chosen = 0;
        var opened = 0;
        icon.CommandInvoked += (_, _) => chosen++;
        icon.MenuPresenter = (_, _, _, _) =>
        {
            opened++;
            return 0;
        };

        Notify(icon, User32.WM_CONTEXTMENU);
        Assert.Equal(0, opened);

        icon.Menu = SampleMenu();
        Notify(icon, User32.WM_CONTEXTMENU);
        Assert.Equal(1, opened);
        Assert.Equal(0, chosen);
    }

    [Fact]
    public void TheIconIsPutBackWhenTheTaskbarIsMadeAgain()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();

        // File Explorer closing takes every icon with it; when it starts, Windows tells every top-level window.
        icon.LoseIconForTest();
        Assert.False(icon.IsInNotificationArea());
        User32.SendMessage(icon.Window, User32.RegisterWindowMessage("TaskbarCreated"), 0, 0);

        Assert.True(icon.IsShown);
        Assert.True(icon.IsInNotificationArea());
    }

    [Fact]
    public void AnIconThatWasHiddenIsNotPutBackWhenTheTaskbarIsMadeAgain()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();
        icon.Hide();

        User32.SendMessage(icon.Window, User32.RegisterWindowMessage("TaskbarCreated"), 0, 0);

        Assert.False(icon.IsShown);
        Assert.False(icon.IsInNotificationArea());
    }

    [Fact]
    public void TheTooltipIsChangedWhileShown_AndLongWordsAreCutToWhatWindowsKeeps()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();

        icon.Tooltip = "Assistant (local AI paused)";
        Assert.Equal("Assistant (local AI paused)", icon.Tooltip);
        Assert.True(icon.IsInNotificationArea());

        icon.Tooltip = new string('x', 500);
        Assert.Equal(127, icon.Tooltip.Length);
        Assert.True(icon.IsInNotificationArea());
    }

    [Fact]
    public void AMenuThatThrowsDoesNotUnwindIntoWindows()
    {
        using var icon = new NotificationAreaIcon("Assistant");
        icon.Show();
        icon.Menu = SampleMenu();
        icon.MenuPresenter = (_, _, _, _) => throw new InvalidOperationException("A presenter that fails.");

        // The exception stops at the icon's window procedure: an unmanaged caller would end the process.
        Notify(icon, User32.WM_CONTEXTMENU);

        Assert.True(icon.IsShown);
    }
}
