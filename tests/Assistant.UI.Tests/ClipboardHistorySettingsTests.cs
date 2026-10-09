using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.QuickSearch.Clipboard;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Clipboard History in Settings (PROJECT_SPEC §4.9, §3.5): the switch, what it keeps, and clearing it --------------------

    [Fact]
    public void WithoutAClipboardHistoryThePrivacyPageHasNothingToShowOrClear() => RunSta(() =>
    {
        var page = CreateSettingsKit().Model.Privacy;

        Assert.False(page.HasClipboardHistory);
        Assert.Equal("", page.ClipboardHistoryStatus);
        Assert.False(page.ClearClipboardHistoryCommand.CanExecute(null));
    });

    [Fact]
    public void ThePrivacyPageSaysWhatTheClipboardHistoryKeepsAndClearsItAtOnce() => RunSta(() =>
    {
        var history = new ClipboardHistory();
        var page = CreateSettingsKit(clipboardHistory: history).Model.Privacy;
        var changes = new List<string?>();
        page.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Assert.True(page.HasClipboardHistory);
        Assert.StartsWith("Off.", page.ClipboardHistoryStatus, StringComparison.Ordinal);
        Assert.False(page.ClearClipboardHistoryCommand.CanExecute(null));

        history.SetEnabled(true);
        Assert.Equal("On. Nothing is kept yet.", page.ClipboardHistoryStatus);

        history.Add("one");
        Assert.Equal("On. 1 item is kept, in memory only.", page.ClipboardHistoryStatus);
        history.Add("two");
        Assert.Equal("On. 2 items are kept, in memory only.", page.ClipboardHistoryStatus);
        Assert.Contains(nameof(Assistant.UI.Settings.PrivacyPage.ClipboardHistoryStatus), changes);
        Assert.True(page.ClearClipboardHistoryCommand.CanExecute(null));

        page.ClearClipboardHistoryCommand.Execute(null);

        Assert.Empty(history.Items);
        Assert.Equal("On. Nothing is kept yet.", page.ClipboardHistoryStatus);
        Assert.False(page.ClearClipboardHistoryCommand.CanExecute(null));
        Assert.True(history.IsEnabled);
    });

    [Fact]
    public void TheClipboardHistorySwitchIsSavedAtOnceStartsOffAndTellsTheAppWhatWasSaved() => RunSta(() =>
    {
        var kit = CreateSettingsKit();
        var told = new List<SettingsSaved>();
        using var subscription = kit.Bus.Subscribe<List<SettingsSaved>, SettingsSaved>(
            told, static (list, saved, _) =>
            {
                list.Add(saved);
                return Task.CompletedTask;
            });
        var item = kit.Model.Permissions[PermissionCapability.ClipboardHistory];
        Assert.False(item.IsOn);
        Assert.True(item.IsAvailable);
        Assert.False(kit.Saved.Permissions.ClipboardHistory);

        item.IsOn = true;
        kit.Settle();

        Assert.True(kit.Saved.Permissions.ClipboardHistory);
        Assert.True(Assert.Single(told).Settings.Permissions.ClipboardHistory);

        item.IsOn = false;
        kit.Settle();

        Assert.False(kit.Saved.Permissions.ClipboardHistory);
        Assert.False(told[^1].Settings.Permissions.ClipboardHistory);
    });
}
