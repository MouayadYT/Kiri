using Assistant.Core.Contracts;
using Assistant.Core.ModelHosting;
using Assistant.ModelHost.FakeEngine;
using Assistant.SmokeTests.Support;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklist 4: the local model answers, and stopping it works. The question goes through the app's real conversation, orchestrator and model service to the
/// model host's real parts and an engine program (<see cref="FakeLocalModel"/>), so the protocol, the streaming and the cancellation are the app's own; only
/// the words are made up.
/// </summary>
public sealed class LocalModelSmokeTests
{
    private const string EngineName = "Assistant.ModelHost.FakeEngine.exe";

    [Fact]
    public Task TheFirstQuestionStartsTheLocalModel_StreamsItsAnswerIn_AndTheEngineIsGoneWhenTheHostIs() => Smoke.RunInFolderAsync(async scratch =>
    {
        var model = FakeLocalModel.Create(scratch);
        await using (var app = await SmokeApp.StartAsync(scratch, model.Replace))
        {
            await app.ChangeSettingsAsync(model.Use);
            var conversation = app.Get<ConversationViewModel>();

            conversation.StartNew("Why is the sky blue?");
            await Wait.UntilAsync(
                () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete,
                () => "The answer did not arrive: " + Describe(conversation, app));

            Assert.Equal("Hello there!", conversation.Messages[1].Text);
            Assert.Equal(1, model.EngineLaunches);
            Assert.Contains("Why is the sky blue?", model.RequestBody(1), StringComparison.Ordinal);
            Assert.Contains(ProcessTree.Descendants(), program => program.Name == EngineName);
            Assert.Equal(ModelStatus.Ready, app.Get<IModelLifecycle>().Current.Status);

            // The next question goes to the model that is already loaded.
            Assert.True(conversation.Ask("And at sunset?"));
            await Wait.UntilAsync(
                () => conversation.Messages.Count == 4 && conversation.Messages[3].Status == MessageStatus.Complete,
                () => "The follow-up was not answered: " + Describe(conversation, app));
            Assert.Equal(1, model.EngineLaunches);

            // What was asked and answered is not in the app's logs (PROJECT_SPEC §3.3).
            Assert.DoesNotContain(app.Logs.Lines, line => line.Contains("sky blue", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Hello there", StringComparison.Ordinal) || line.Contains("sunset", StringComparison.OrdinalIgnoreCase));
        }

        // The app has let go of the host; the host's own shutdown ends the engine it started, so no program of the model is left running.
        Assert.Contains(ProcessTree.Descendants(), program => program.Name == EngineName);
        await model.DisposeAsync();
        await Wait.UntilAsync(
            () => ProcessTree.Descendants().All(program => program.Name != EngineName), "The engine outlived the host that started it.");
    });

    [Fact]
    public Task StoppingAnAnswer_StopsTheEngine_AndTheNextQuestionIsAnsweredAtOnce() => RunWithAModelAsync(
        new FakeEngineScenario { Chat = new FakeChatReply { Pieces = [.. Enumerable.Repeat("word ", 400)], PieceDelayMs = 25 } },
        async (app, model) =>
        {
            var conversation = app.Get<ConversationViewModel>();
            conversation.StartNew("Write me a very long story.");
            await Wait.UntilAsync(
                () => conversation.Messages.Count == 2 && conversation.Messages[1].Text.StartsWith("word", StringComparison.Ordinal),
                () => "The answer did not start: " + Describe(conversation, app));
            Assert.True(conversation.IsAnswering);

            conversation.StopCommand.Execute(null);

            // What it had said stays, the answer says it was stopped, and the engine saw the client go.
            await Wait.UntilAsync(
                () => conversation.Messages[1].Status == MessageStatus.Stopped && !conversation.IsAnswering,
                () => "The answer was not stopped: " + Describe(conversation, app));
            Assert.StartsWith("word", conversation.Messages[1].Text, StringComparison.Ordinal);
            await Wait.UntilAsync(() => model.EngineSawTheClientLeave(1), "The engine was left generating after the stop.");

            // Not refused as busy, and not a second engine.
            Assert.True(conversation.Ask("Never mind. Say hello."));
            await Wait.UntilAsync(
                () => conversation.Messages.Count == 4 && conversation.Messages[3].Text.StartsWith("word", StringComparison.Ordinal),
                () => "The next question was not answered after a stop: " + Describe(conversation, app));
            Assert.Equal(1, model.EngineLaunches);
            conversation.Stop();
        });

    [Fact]
    public Task WithoutAnyModelInstalled_TheAnswerSaysHowToSetOneUp_AndNothingIsStarted() => Smoke.RunAsync(async app =>
    {
        var conversation = app.Get<ConversationViewModel>();

        conversation.StartNew("Hello?");
        await Wait.UntilAsync(
            () => conversation.Messages.Count == 2 && conversation.Messages[1].Status == MessageStatus.Complete,
            () => "The app did not answer: " + Describe(conversation, app));

        Assert.Equal(ModelAnswerProvider.NoModelText, conversation.Messages[1].Text);
        Assert.DoesNotContain(ProcessTree.Descendants(), program => program.Name == EngineName);
    });

    // How things stand, for a failure's message: the statuses and the words (made up by the checks) and what the app logged about the model.
    private static string Describe(ConversationViewModel conversation, SmokeApp app) =>
        string.Join(" | ", conversation.Messages.Select(message => $"{message.Role} {message.Status}: {message.Text}"))
        + " || " + string.Join(" // ", app.Logs.Lines.Where(line => !line.StartsWith("Debug", StringComparison.Ordinal)).TakeLast(8));

    private static Task RunWithAModelAsync(FakeEngineScenario scenario, Func<SmokeApp, FakeLocalModel, Task> body) =>
        Smoke.RunInFolderAsync(async scratch =>
        {
            await using var model = FakeLocalModel.Create(scratch, scenario);
            await using var app = await SmokeApp.StartAsync(scratch, model.Replace);
            await app.ChangeSettingsAsync(model.Use);
            await body(app, model);
        });
}
