using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Assistant.SmokeTests.Support;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklist 5: what was asked and answered is there after the app is closed and opened again, can be searched, and is not written at all while history
/// is off. The conversations are made through the app's own panel, saved by its recorder into its SQLite database, and read back by a second start of the
/// app over the same data folder.
/// </summary>
public sealed class HistorySmokeTests
{
    private static readonly FakeEngineScenario QuokkaAnswer = new()
    {
        Chat = new FakeChatReply { Pieces = ["Quokkas are small ", "marsupials from Western Australia."] },
    };

    [Fact]
    public Task AConversationIsThereAfterARestart_WithItsMessagesAndCard_AndTheSearchFindsIt() => Smoke.RunInFolderAsync(async scratch =>
    {
        await using var model = FakeLocalModel.Create(scratch, QuokkaAnswer);
        Guid sum;
        Guid quokkas;
        await using (var app = await SmokeApp.StartAsync(scratch, model.Replace))
        {
            await app.ChangeSettingsAsync(model.Use);
            var panel = app.Get<ConversationViewModel>();

            panel.StartNew("9 + 10");
            await Wait.UntilAsync(() => panel.Messages.Count == 2 && panel.Messages[1].Status == MessageStatus.Complete, "The sum was not answered.");
            sum = panel.Id;

            panel.StartNew("Tell me about quokkas");
            await Wait.UntilAsync(
                () => panel.Messages.Count == 2 && panel.Messages[1].Status == MessageStatus.Complete && panel.Messages[1].Text.Length > 0,
                "The model did not answer.");
            quokkas = panel.Id;
            Assert.True(await app.Get<ConversationRecorder>().FlushAsync(TimeSpan.FromSeconds(10)), "The conversation was not saved.");
        }

        // A new run of the app over the same data folder: nothing is remembered but what the database holds.
        await using (var app = await SmokeApp.StartAsync(scratch))
        {
            Assert.False(app.FirstRun);
            var history = app.Get<HistoryViewModel>();
            await history.RefreshAsync();

            Assert.Equal(2, history.Conversations.Count);
            var sumCard = history.Conversations.Single(card => card.Id == sum);
            var quokkaCard = history.Conversations.Single(card => card.Id == quokkas);
            Assert.Equal("9 + 10", sumCard.Title);
            Assert.Equal("Tell me about quokkas", quokkaCard.Title);
            Assert.Equal("Quokkas are small marsupials from Western Australia.", quokkaCard.Preview);

            history.Selected = sumCard;
            await Wait.UntilAsync(() => sumCard.IsLoaded, "The saved messages were not read.");
            Assert.Equal(["9 + 10"], sumCard.Messages.Where(message => message.Role == Assistant.Core.Domain.MessageRole.User).Select(message => message.Text));
            Assert.Equal("19", sumCard.Messages[1].Content.OfType<CalculationResult>().Single().Result);
            Assert.Equal(MessageStatus.Complete, sumCard.Messages[1].Status);

            // The search reads the words of the questions and of the answers.
            history.SearchText = "marsupials";
            await history.WhenSearchSettledAsync();
            Assert.Equal([quokkas], history.Conversations.Where(card => card.IsShown).Select(card => card.Id));
            history.SearchText = "";
            await history.WhenSearchSettledAsync();
            Assert.Equal(2, history.Conversations.Count(card => card.IsShown));
        }
    });

    [Fact]
    public Task WithHistoryTurnedOff_NothingOfAConversationIsWritten_AndNothingIsThereAfterARestart() => Smoke.RunInFolderAsync(async scratch =>
    {
        await using (var app = await SmokeApp.StartAsync(scratch))
        {
            await app.ChangeSettingsAsync(settings => settings with { Privacy = settings.Privacy with { HistoryEnabled = false } });
            var panel = app.Get<ConversationViewModel>();

            panel.StartNew("9 + 10");
            await Wait.UntilAsync(() => panel.Messages.Count == 2 && panel.Messages[1].Status == MessageStatus.Complete, "The sum was not answered.");
            Assert.True(await app.Get<ConversationRecorder>().FlushAsync(TimeSpan.FromSeconds(10)));
        }

        await using (var app = await SmokeApp.StartAsync(scratch))
        {
            var history = app.Get<HistoryViewModel>();
            await history.RefreshAsync();

            Assert.Empty(history.Conversations);
            Assert.True((await app.Get<Assistant.Core.Contracts.ISettingsService>().LoadAsync()).Privacy is { HistoryEnabled: false });
        }
    });
}
