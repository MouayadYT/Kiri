using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Memory;
using Assistant.UI.Settings;
using Xunit;

namespace Assistant.UI.Tests;

// Settings > Memory: what the Assistant remembers for the user, listed, rewritten, added to and forgotten.
public sealed partial class PromptInputControlTests
{
    private static readonly System.DateTimeOffset MemoryNow = new(2026, 10, 7, 12, 0, 0, System.TimeSpan.Zero);

    private static (SettingsKit Kit, InMemoryMemoryStore Store) MemoryKit(params MemoryEntry[] entries)
    {
        var store = new InMemoryMemoryStore();
        foreach (var entry in entries)
        {
            store.SaveAsync(entry).GetAwaiter().GetResult();
        }

        return (CreateSettingsKit(remembered: store), store);
    }

    private static MemoryEntry Remembered(MemoryKind kind, string text, string key = "k", string value = "v", int minute = 0) =>
        new(System.Guid.NewGuid(), kind, text, MemoryNow.AddMinutes(minute)) { Key = kind == MemoryKind.Note ? string.Empty : key, Value = kind == MemoryKind.Note ? string.Empty : value };

    [Fact]
    public void MemoryIsASectionOfTheSettingsWindowRightAfterPeople() => RunSta(() =>
    {
        var kit = CreateSettingsKit();

        var titles = kit.Model.Sections.Select(section => section.Title).ToList();

        Assert.Equal(titles.IndexOf("People") + 1, titles.IndexOf("Memory"));
        kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Memory);
        Assert.Same(kit.Model.Memory, kit.Model.CurrentPage);
        Assert.False(kit.Model.Memory.HasStore);
        Assert.False(kit.Model.Memory.AddCommand.CanExecute(null));
    });

    [Fact]
    public void WhatIsRememberedIsListedNewestFirst_AndEveryOneOfThemCanBeEdited() => RunSta(() =>
    {
        var (kit, _) = MemoryKit(
            Remembered(MemoryKind.Note, "The user's sister is called Lena."),
            Remembered(MemoryKind.HomeDevice, "When you say \"AC\" at home, you mean Bedroom Thermostat.", "ac", "climate.bedroom", 1),
            Remembered(MemoryKind.MessageRoute, "Messages to Sami go through Beeper.", "person", "chat", 2),
            Remembered(MemoryKind.Preference, "The Clock window (alarms and timers) opens on Display 2, on the left.", "clock.display", "x|left", 3));

        var page = kit.Model.Memory;

        Assert.True(page.HasItems);
        Assert.Equal(["Preference", "Messages", "Home", "Note"], page.Items.Select(item => item.KindLabel));
        Assert.Equal([true, true, true, true], page.Items.Select(item => item.CanEdit));

        // What stands for something says what to type; a note is just rewritten.
        Assert.Equal([true, true, true, false], page.Items.Select(item => item.HasEditHint));
        Assert.Contains("percentages", page.Items[0].EditHint, System.StringComparison.Ordinal);

    });

    [Fact]
    public void WhichChatIsAPersonsOnAServiceIsListedUnderMessages_CanBeForgotten_AndIsNotSomethingToType() => RunSta(() =>
    {
        var (kit, store) = MemoryKit(
            Remembered(MemoryKind.MessageRoute, "Messages to Sami go through Beeper.", "person", "chat"),
            Remembered(MemoryKind.MessageChat, "On iMessage in Beeper, messages to Sami go to their chat.", "person:imessage", "chat", 1));
        var page = kit.Model.Memory;
        var learned = page.Items.Single(item => item.Text.StartsWith("On iMessage", System.StringComparison.Ordinal));

        Assert.Equal("Messages", learned.KindLabel);
        Assert.False(learned.CanEdit);
        Assert.False(learned.EditCommand.CanExecute(null));
        Assert.True(page.Items.Single(item => !ReferenceEquals(item, learned)).CanEdit);

        learned.ForgetCommand.Execute(null);
        SettingsUntil(() => store.Entries.Count == 1, "the chat was forgotten");
        Assert.Equal(MemoryKind.MessageRoute, store.Entries.Single().Kind);
    });

    [Fact]
    public void ANoteIsRewritten_Added_AndForgotten_AndTheFileFollows() => RunSta(() =>
    {
        var (kit, store) = MemoryKit(Remembered(MemoryKind.Note, "The user likes tea."));
        var page = kit.Model.Memory;
        var note = page.Items.Single();

        note.EditCommand.Execute(null);
        Assert.True(note.IsEditing);
        note.Text = "The user likes green tea.";
        note.SaveCommand.Execute(null);
        Pump();

        Assert.False(note.IsEditing);
        Assert.Equal("The user likes green tea.", store.Entries.Single().Text);

        // A note typed in by hand, and nothing kept of an empty one.
        Assert.False(page.AddCommand.CanExecute(null));
        page.NewText = "  My desk is in the study. ";
        page.AddCommand.Execute(null);
        Pump();

        Assert.Equal(string.Empty, page.NewText);
        Assert.Equal(["My desk is in the study.", "The user likes green tea."], page.Items.Select(item => item.Text));

        page.Items[1].ForgetCommand.Execute(null);
        Pump();

        Assert.Equal(["My desk is in the study."], store.Entries.Select(entry => entry.Text));
        Assert.Single(page.Items);
    });

    [Fact]
    public void ARewriteThatIsGivenUpLeavesTheNoteAsItWasKept() => RunSta(() =>
    {
        var (kit, store) = MemoryKit(Remembered(MemoryKind.Note, "The user likes tea."));
        var note = kit.Model.Memory.Items.Single();

        note.EditCommand.Execute(null);
        note.Text = "Something else entirely";
        note.CancelCommand.Execute(null);

        Assert.False(note.IsEditing);
        Assert.Equal("The user likes tea.", note.Text);
        Assert.Equal("The user likes tea.", store.Entries.Single().Text);
    });

    [Fact]
    public void WhatIsRememberedInAConversationShowsWhileTheWindowIsOpen_WithoutTouchingANoteBeingRewritten() => RunSta(() =>
    {
        var (kit, store) = MemoryKit(Remembered(MemoryKind.Note, "The user likes tea."));
        var page = kit.Model.Memory;
        var note = page.Items.Single();
        note.EditCommand.Execute(null);
        note.Text = "The user likes gre";

        store.SaveAsync(MemoryEntry.Note("The user's sister is called Lena.", MemoryNow.AddHours(1))).GetAwaiter().GetResult();
        Pump();

        Assert.Equal(["The user's sister is called Lena.", "The user likes gre"], page.Items.Select(item => item.Text));
        Assert.True(note.IsEditing);
    });

    [Fact]
    public void TheMemoryPageIsDrawnWithWhatIsRememberedItsButtonsAndNoBindingErrors() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new System.Uri("/Assistant.UI;component/Themes/Theme.xaml", System.UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var (kit, _) = MemoryKit(
            Remembered(MemoryKind.Note, "The user's sister is called Lena."),
            Remembered(MemoryKind.HomeDevice, "When you say \"AC\" at home, you mean Bedroom Thermostat.", "ac", "climate.bedroom", 1),
            Remembered(MemoryKind.MessageRoute, "Messages to Sami go through Beeper.", "person", "chat", 2));
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Memory);
            Pump();
            Pump();

            var texts = AllTextOf(window).ToList();
            Assert.Contains("Remembered", texts);
            Assert.Contains("The user's sister is called Lena.", texts);
            Assert.Contains("Messages to Sami go through Beeper.", texts);
            Assert.Contains("Add a note", texts);
            // Edit is beside Forget on every row.
            Assert.Equal(3, Descendants<Button>(window).Count(button => button.Content as string == "Edit" && button.IsVisible));
            Assert.Equal(3, Descendants<Button>(window).Count(button => button.Content as string == "Forget" && button.IsVisible));
            Assert.True(ButtonsVisible(window, "Add"));
            RenderFixture(window, "settings-memory.png", 2);

            kit.Model.Memory.Items.Single(item => !item.HasEditHint).EditCommand.Execute(null);
            Pump();

            Assert.True(ButtonsVisible(window, "Save"));
            Assert.True(ButtonsVisible(window, "Cancel"));
            Assert.Contains(Descendants<TextBox>(window), box => box.IsVisible && box.Text == "The user's sister is called Lena.");
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });

    private const string ClockEntry = "The Clock window (alarms and timers) opens on Display 1, on the left, 94% across and 36% down.";

    [Theory]
    [InlineData("The Clock window (alarms and timers) opens on Display 1, on the left, 50% across and 20% down.", 50, 20)]
    [InlineData("10% across and 80% down", 10, 80)]
    [InlineData("on Display 1, 100 0", 100, 0)]
    public void WhereTheClockWindowOpensIsEditedByItsTwoPercentages(string typed, int across, int down) => RunSta(() =>
    {
        var (kit, store) = MemoryKit(Remembered(MemoryKind.Preference, ClockEntry, Assistant.Core.Clock.ClockPlace.Key, "DISPLAY1|left|0.94|0.36"));
        var page = kit.Model.Memory;
        var place = page.Items.Single();

        place.EditCommand.Execute(null);
        place.Text = typed;
        place.SaveCommand.Execute(null);
        Pump();

        Assert.False(place.IsEditing);
        Assert.Equal(string.Empty, page.Notice);
        var kept = store.Entries.Single();
        Assert.Equal($"The Clock window (alarms and timers) opens on Display 1, on the left, {across}% across and {down}% down.", kept.Text);
        Assert.Equal(kept.Text, place.Text);

        // What the Clock window is placed by is the value, and it follows: the same display, the new spot.
        var spot = Assistant.Core.Clock.ClockPlace.ReadSpot(store);
        Assert.NotNull(spot);
        Assert.Equal((across / 100.0, down / 100.0), (System.Math.Round(spot.X, 2), System.Math.Round(spot.Y, 2)));
        Assert.StartsWith("DISPLAY1|left|", kept.Value, System.StringComparison.Ordinal);
    });

    [Theory]
    [InlineData("somewhere on the right")]
    [InlineData("140% across and 20% down")]
    public void APlaceForTheClockThatCannotBeReadIsSaidAndChangesNothing(string typed) => RunSta(() =>
    {
        var (kit, store) = MemoryKit(Remembered(MemoryKind.Preference, ClockEntry, Assistant.Core.Clock.ClockPlace.Key, "DISPLAY1|left|0.94|0.36"));
        var page = kit.Model.Memory;
        var place = page.Items.Single();

        place.EditCommand.Execute(null);
        place.Text = typed;
        place.SaveCommand.Execute(null);
        Pump();

        Assert.Contains("two percentages", page.Notice, System.StringComparison.Ordinal);
        Assert.True(place.IsEditing);
        Assert.Equal((ClockEntry, "DISPLAY1|left|0.94|0.36"), (store.Entries.Single().Text, store.Entries.Single().Value));
    });

    [Fact]
    public void WhatAWordMeansAtHomeIsEditedByNamingAnotherDeviceOfTheUsersHome() => RunSta(() =>
    {
        var store = new InMemoryMemoryStore();
        store.SaveAsync(Remembered(MemoryKind.HomeDevice, "When you say \"ac\" at home, you mean AC IR Bridge.", "ac", "remote.ac_ir_bridge")).GetAwaiter().GetResult();
        var home = new FakeHomeAssistant().ConnectedAt("http://192.168.1.20:8123").With(
            new Assistant.Core.Home.HomeDevice("remote.ac_ir_bridge", "AC IR Bridge", "on"),
            new Assistant.Core.Home.HomeDevice("climate.bedroom", "Bedroom Thermostat", "cool"),
            new Assistant.Core.Home.HomeDevice("fan.window", "Window Fan", "off"));
        var kit = CreateSettingsKit(remembered: store, home: home);
        var page = kit.Model.Memory;
        var device = page.Items.Single();

        // A device that is not in the home is said, and nothing changes.
        device.EditCommand.Execute(null);
        device.Text = "When you say \"ac\" at home, you mean the big one.";
        device.SaveCommand.Execute(null);
        Pump();
        Assert.Contains("one device of your Home Assistant", page.Notice, System.StringComparison.Ordinal);
        Assert.Equal("remote.ac_ir_bridge", store.Entries.Single().Value);

        // The name of another one is taken, with or without the sentence around it.
        device.Text = "When you say \"ac\" at home, you mean bedroom thermostat.";
        device.SaveCommand.Execute(null);
        Pump();
        Assert.Equal(string.Empty, page.Notice);
        Assert.Equal(("ac", "climate.bedroom", "When you say \"ac\" at home, you mean Bedroom Thermostat."), (store.Entries.Single().Key, store.Entries.Single().Value, store.Entries.Single().Text));

        device.EditCommand.Execute(null);
        device.Text = "Window Fan";
        device.SaveCommand.Execute(null);
        Pump();
        Assert.Equal(("fan.window", "When you say \"ac\" at home, you mean Window Fan."), (store.Entries.Single().Value, store.Entries.Single().Text));
    });

    [Fact]
    public void WhereSomeonesMessagesGoIsEditedByNamingTheService() => RunSta(() =>
    {
        var (kit, store) = MemoryKit(Remembered(MemoryKind.MessageRoute, "Messages to Marcus go through Beeper.", "0f8fad5bd9cb469fa16570867728950e", "{\"i\":\"beeper\",\"c\":\"!room\",\"q\":\"Marcus\"}"));
        var page = kit.Model.Memory;
        var route = page.Items.Single();

        route.EditCommand.Execute(null);
        route.Text = "Messages to Marcus go through a pigeon.";
        route.SaveCommand.Execute(null);
        Pump();
        Assert.Contains("iMessage, WhatsApp or Beeper", page.Notice, System.StringComparison.Ordinal);
        Assert.Contains("!room", store.Entries.Single().Value, System.StringComparison.Ordinal);

        route.Text = "Messages to Marcus go through imessage.";
        route.SaveCommand.Execute(null);
        Pump();

        // The service is what is kept: the chat on it is found each time, as if the user had said "on iMessage".
        var kept = store.Entries.Single();
        Assert.Equal(string.Empty, page.Notice);
        Assert.Equal("Messages to Marcus go through iMessage.", kept.Text);
        Assert.Equal("0f8fad5bd9cb469fa16570867728950e", kept.Key);
        Assert.Equal(Assistant.Tools.Messaging.MessageRoutes.ForService("iMessage"), kept.Value);
    });

    [Fact]
    public void WithNothingRememberedTheMemoryPageSaysWhatItIsFor() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new System.Uri("/Assistant.UI;component/Themes/Theme.xaml", System.UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var (kit, _) = MemoryKit();
        var (window, _, _) = CreateSettingsWindow(kit);
        try
        {
            window.Show();
            kit.Model.SelectedSection = kit.Model.Sections.Single(section => section.Section == SettingsSection.Memory);
            Pump();

            Assert.Contains(AllTextOf(window), text => text == "Nothing yet.");
            Assert.True(ButtonsVisible(window, "Add"));
            Assert.Empty(errors.Messages);
        }
        finally
        {
            window.CloseForGood();
            app.Resources.MergedDictionaries.Clear();
        }
    });
}
