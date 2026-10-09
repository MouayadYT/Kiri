using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.QuickSearch;
using Assistant.Core.Settings;
using Assistant.UI.History;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The settings that used to say "Coming later" and now do their work: the folders kept out of search can be added and taken away, "Search files by default" starts the bar on Files,
/// and "Keep history for" deletes what is older.
/// </summary>
public sealed partial class PromptInputControlTests
{
    [Fact]
    public void FoldersAreKeptOutOfSearchByAddingThemAndLetBackByRemovingThem() => RunSta(() =>
    {
        var kit = CreateSettingsKit(new AppSettings { Privacy = new PrivacySettings { ExcludedFolders = [@"C:\Taxes"] } });
        var picked = new Queue<string?>([@"D:\Private\", @"C:\TAXES", null, @"D:\Private"]);
        kit.Model.Privacy.PickFolder = () => picked.Dequeue();

        kit.Model.Privacy.AddExcludedFolderCommand.Execute(null);
        kit.Settle();
        Assert.Equal([@"C:\Taxes", @"D:\Private"], kit.Saved.Privacy.ExcludedFolders);

        // The same folder in another case, one the dialog gave up on, and one that is there already add nothing.
        kit.Model.Privacy.AddExcludedFolderCommand.Execute(null);
        kit.Model.Privacy.AddExcludedFolderCommand.Execute(null);
        kit.Model.Privacy.AddExcludedFolderCommand.Execute(null);
        kit.Settle();
        Assert.Equal([@"C:\Taxes", @"D:\Private"], kit.Saved.Privacy.ExcludedFolders);

        kit.Model.Privacy.RemoveExcludedFolderCommand.Execute(@"c:\taxes");
        kit.Settle();
        Assert.Equal([@"D:\Private"], kit.Saved.Privacy.ExcludedFolders);
        Assert.True(kit.Model.Privacy.HasExcludedFolders);

        kit.Model.Privacy.RemoveExcludedFolderCommand.Execute(@"D:\Private");
        kit.Settle();
        Assert.Empty(kit.Saved.Privacy.ExcludedFolders);
        Assert.False(kit.Model.Privacy.HasExcludedFolders);
    });

    private sealed class InMemorySettings(AppSettings settings) : ISettingsService
    {
        public AppSettings Current { get; set; } = settings;

        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);

        public Task SaveAsync(AppSettings value, CancellationToken cancellationToken = default)
        {
            Current = value;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void TheBarStartsOnFilesWhenTheUserAskedForItAndOtherwiseListsEverything() => RunSta(() =>
    {
        var settings = new InMemorySettings(new AppSettings { Ui = new UiSettings { FilesScopeOnByDefault = true } });
        var results = new SearchResultsViewModel(settings: settings);

        results.ResetScope();
        SettingsWait(Task.Delay(50));
        Assert.Equal(QuickSearchResultType.Files, results.Scope);

        // The user pressed Esc: everything is listed until the bar is opened again.
        results.SetScope(null);
        Assert.Null(results.Scope);
        results.ResetScope();
        SettingsWait(Task.Delay(50));
        Assert.Equal(QuickSearchResultType.Files, results.Scope);

        settings.Current = new AppSettings { Ui = new UiSettings { FilesScopeOnByDefault = false } };
        results.ResetScope();
        SettingsWait(Task.Delay(50));
        Assert.Null(results.Scope);

        // With no settings at all there is nothing to start on.
        var plain = new SearchResultsViewModel();
        plain.SetScope(QuickSearchResultType.Actions);
        plain.ResetScope();
        Assert.Null(plain.Scope);
    });

    [Fact]
    public async Task TheBarListsNoMoreInAGroupThanTheUserChoseAndAnotherNumberIsPickedUpAtOnce()
    {
        var provider = new FakeQuickProvider(
            "files", QuickSearchResultType.Files, answer: _ => [.. Enumerable.Range(1, 9).Select(index =>
                new QuickSearchResult("file:" + index, QuickSearchResultType.Files, "files", "Note " + index, new QuickSearchAction(QuickSearchActionKind.OpenPath, "Open", "C:\n" + index + ".txt")))]);
        var settings = new InMemorySettings(new AppSettings { Ui = new UiSettings { BarResultsPerGroup = 2 } });
        var kit = new QuickKit([provider], settings: settings);

        var two = (await kit.SearchAsync("note"))[^1];
        settings.Current = new AppSettings { Ui = new UiSettings { BarResultsPerGroup = 4 } };
        var four = (await kit.SearchAsync("note"))[^1];

        Assert.True(two.Sections.Sum(section => section.Items.Count) <= 3);
        Assert.True(four.Sections.Sum(section => section.Items.Count) is > 3 and <= 5);
    }

    // ---- Keep history for ----

    private static readonly DateTimeOffset RetentionNow = new(2026, 10, 4, 12, 0, 0, TimeSpan.Zero);

    private sealed class DeletingHistory : IConversationService
    {
        public List<ConversationSummary> Summaries { get; } = [];

        public List<Guid> Deleted { get; } = [];

        public Task<IReadOnlyList<ConversationSummary>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationSummary>>([.. Summaries.Where(summary => !Deleted.Contains(summary.Id))]);

        public Task<Conversation?> GetAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<Conversation?>(null);

        public Task<IReadOnlyList<ConversationSearchResult>> SearchAsync(string query, int limit = IConversationService.DefaultSearchLimit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ConversationSearchResult>>([]);

        public Task SaveAsync(Conversation conversation, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveMessageAsync(Guid conversationId, Message message, DateTimeOffset changedAt, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RenameAsync(Guid id, string title, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            Deleted.Add(id);
            return Task.CompletedTask;
        }

        public Task DeleteAllAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static ConversationSummary Aged(int daysOld) =>
        new(Guid.NewGuid(), "Chat", RetentionNow.AddDays(-daysOld - 1), RetentionNow.AddDays(-daysOld), 2);

    [Theory]
    [InlineData(HistoryRetention.UntilDeleted, 0)]
    [InlineData(HistoryRetention.SevenDays, 2)]
    [InlineData(HistoryRetention.ThirtyDays, 1)]
    [InlineData(HistoryRetention.NinetyDays, 0)]
    public async Task OnlyConversationsOlderThanTheChosenTimeAreDeleted(HistoryRetention retention, int deleted)
    {
        var history = new DeletingHistory();
        history.Summaries.AddRange([Aged(1), Aged(6), Aged(10), Aged(45)]);
        var settings = new InMemorySettings(new AppSettings());
        using var service = new HistoryRetentionService(history, settings, new SteppingClock(RetentionNow), NullLogger<HistoryRetentionService>.Instance);

        var count = await service.PruneAsync(retention, CancellationToken.None);

        Assert.Equal(deleted, count);
        Assert.Equal(deleted, history.Deleted.Count);

        // A conversation that is still going on (updated just now) is never one of them, whatever its age.
        Assert.DoesNotContain(history.Deleted, id => history.Summaries.First(summary => summary.Id == id).UpdatedAt > RetentionNow.AddDays(-7));
    }
}
