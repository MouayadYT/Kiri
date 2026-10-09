using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.Windows.Imaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ShapePath = System.Windows.Shapes.Path;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- The warning that context did not fit (PROJECT_SPEC §5.5) --------------------------------------------------------

    // The provider over the real orchestrator, prompt builder and context service, with the user's limits as given.
    private static (ModelAnswerProvider Answers, IContextService Contexts) WarningAnswers(
        ScriptedModel model, ContextLimitSettings limits)
    {
        var settings = new InMemorySettingsService();
        settings.SaveAsync(new AppSettings { ContextLimits = limits }).GetAwaiter().GetResult();
        var contexts = new ContextService(new Assistant.Core.Budgeting.ContextBudgeter(new Assistant.Core.Budgeting.HeuristicTokenEstimator()));
        var answers = new ModelAnswerProvider(
            new AssistantOrchestrator(
                model, settings, new PromptBuilder(contexts), new ImagePreprocessor(), new FixedClock(Now),
                NullLogger<AssistantOrchestrator>.Instance, contexts),
            new FixedClock(Now), null, null, contexts);
        return (answers, contexts);
    }

    private static ContextItem LongFile(string name, int words) =>
        new(Guid.NewGuid(), ContextItemType.File, name) { Text = string.Join(' ', Enumerable.Repeat("word", words)) };

    [Fact]
    public void TheAppHasOneContextService_ForEveryoneWhoSuppliesOrFitsContext()
    {
        // Building the host validates every registration.
        using var host = AppHost.Create();

        var contexts = host.Services.GetRequiredService<IContextService>();

        Assert.IsType<ContextService>(contexts);
        Assert.Same(contexts, host.Services.GetRequiredService<IContextService>());
        Assert.NotNull(host.Services.GetRequiredService<PromptBuilder>());
        Assert.NotNull(host.Services.GetRequiredService<IAssistantOrchestrator>());
        Assert.NotNull(host.Services.GetRequiredService<ModelAnswerProvider>());
    }

    [Fact]
    public void WhenContextDoesNotFit_TheAnswerStartsWithAWarningThatNamesIt_ApartFromTheProse() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var (answers, _) = WarningAnswers(model, new ContextLimitSettings { HeavyContextTokens = 1000, ReservedOutputTokens = 200 });
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(
            Guid.NewGuid(), "Summarize", [LongFile("Minutes.txt", 6000)], null, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Here it is."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        var answer = Assert.Single(shown);
        var warning = Assert.IsType<ContextWarningContent>(answer.Content[0]);
        Assert.Single(warning.Lines);
        Assert.Contains("“Minutes.txt”", warning.Lines[0], StringComparison.Ordinal);
        Assert.StartsWith("Warning: ", warning.Text, StringComparison.Ordinal);
        Assert.IsType<TextContent>(answer.Content[1]);

        // The warning is not the model's prose, so it is not what is copied or saved as the answer's text.
        Assert.Equal("Here it is.", answer.Text);
        Assert.Equal(MessageStatus.Complete, answer.Status);
    });

    [Fact]
    public void WhenEverythingFits_ThereIsNoWarning() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var (answers, _) = WarningAnswers(model, new ContextLimitSettings { HeavyContextTokens = 1000, ReservedOutputTokens = 200 });
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(
            Guid.NewGuid(), "Summarize", [LongFile("Short.txt", 40)], null, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Done."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Empty(Assert.Single(shown).Content.OfType<ContextWarningContent>());
    });

    [Fact]
    public void SeveralThingsThatDidNotFit_AreOneWarningWithALineForEach() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var (answers, _) = WarningAnswers(model, new ContextLimitSettings { HeavyContextTokens = 600, ReservedOutputTokens = 100 });
        var shown = new List<MessageViewModel>();
        var selected = LongFile("Selected.txt", 4000);
        var retrieved = new ContextItem(Guid.NewGuid(), ContextItemType.SearchResults, "Found files")
        {
            Text = string.Join(' ', Enumerable.Repeat("match", 300)),
        };

        var asked = answers.StreamAnswerAsync(
            Guid.NewGuid(), "Summarize", [retrieved, selected], null, shown.Add, CancellationToken.None);
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        model.Write(AssistantResponseChunk.ForTextDelta("Done."));
        model.End();
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        // The file the user selected is served first, so it is the one that is cut short, and the search results, which ranked
        // after it, are what did not fit.
        var warning = Assert.Single(Assert.Single(shown).Content.OfType<ContextWarningContent>());
        Assert.Equal(2, warning.Lines.Count);
        Assert.Contains("“Selected.txt”", warning.Lines[0], StringComparison.Ordinal);
        Assert.Contains("“Found files”", warning.Lines[1], StringComparison.Ordinal);
    });

    [Fact]
    public void ContextThatWasSuppliedForAQuestionThatNeverStarted_DoesNotWaitForTheNext() => RunSta(() =>
    {
        var model = new ScriptedModel { Active = null };
        var (answers, contexts) = WarningAnswers(model, new ContextLimitSettings());
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();

        var asked = answers.StreamAnswerAsync(conversation, "Summarize", [LongFile("A.txt", 10)], null, shown.Add, CancellationToken.None);
        WaitUntil(() => asked.IsCompleted, "The answer did not end.");

        Assert.Empty(contexts.PendingItems(conversation));
        Assert.Contains("No local model", Assert.Single(shown).Text, StringComparison.Ordinal);
    });

    [Fact]
    public void TheWarningIsSavedWithTheAnswer_AndComesBackWithIt()
    {
        var mapper = new MessageMapper(new RecordingClipboard());
        var answer = new MessageViewModel(MessageRole.Assistant) { CreatedAt = Now };
        answer.Content.Add(new ContextWarningContent(["“Big.txt” was too long for the model, so only the first part of it was used."]));
        answer.Content.Add(new TextContent("The answer."));

        var saved = mapper.ToDomain(answer);
        var restored = mapper.ToViewModel(saved);

        Assert.Equal("The answer.", saved.Text);
        Assert.Equal("context_warning", Assert.Single(saved.Cards).Kind);
        Assert.Equal(0, saved.Cards[0].TextOffset);
        var warning = Assert.IsType<ContextWarningContent>(restored.Content[0]);
        Assert.Equal(["“Big.txt” was too long for the model, so only the first part of it was used."], warning.Lines);
        Assert.Equal("The answer.", restored.Text);
    }

    [Fact]
    public void ADamagedSavedWarning_IsLeftOut_WithoutLosingTheAnswer()
    {
        var mapper = new MessageMapper(new RecordingClipboard());
        var saved = new Message(Guid.NewGuid(), MessageRole.Assistant, "The answer.", Now)
        {
            Cards = [new CardMetadata("context_warning", "{\"lines\": 5}", 0)],
        };

        var restored = mapper.ToViewModel(saved);

        Assert.Empty(restored.Content.OfType<ContextWarningContent>());
        Assert.Equal("The answer.", restored.Text);
    }

    [Fact]
    public void AWarningWithNothingToSay_IsNotDrawnOrKept()
    {
        var empty = new ContextWarningContent([" ", ""]);
        var answer = new MessageViewModel(MessageRole.Assistant) { CreatedAt = Now };
        answer.Content.Add(empty);

        Assert.Empty(empty.Lines);
        Assert.Empty(new MessageMapper(new RecordingClipboard()).ToDomain(answer).Cards);
        Assert.Equal("ContextWarningContent", empty.ToString());
        Assert.False(empty.IsWide);
    }

    [Fact]
    public void TheWarningIsDrawnAsAGlyphAndWords_InTheTextColumn_AndNeverInACard() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartNew("Synthetic question");
        var answer = new MessageViewModel(MessageRole.Assistant);
        answer.Content.Add(new ContextWarningContent(
            ["“Synthetic.txt” was too long for the model, so only the first part of it was used.", "Earlier messages were left out."]));
        answer.Content.Add(new TextContent("Synthetic prose."));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            Pump();
            var area = Named<Grid>(panel, "ConversationLayer");
            var transcript = Named<FadingScrollViewer>(panel, "Transcript");
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            RenderGlass(panel, "context-warning.png", 2);

            var first = TextNamed(area, "“Synthetic.txt” was too long for the model, so only the first part of it was used.");
            var second = TextNamed(area, "Earlier messages were left out.");
            var prose = TextNamed(area, "Synthetic prose.");
            var glyph = Descendants<ShapePath>(transcript).Single(path => path.Stroke is SolidColorBrush { Color: { R: 0xE8, G: 0xB7, B: 0x5A } });

            // The glyph is the first thing on the line, the words follow it in the text column, and the lines are one above the other.
            Assert.Equal(30, BoundsIn(area, glyph).Left, 1);
            Assert.True(BoundsIn(area, first).Left > BoundsIn(area, glyph).Right);
            Assert.Equal(BoundsIn(area, first).Left, BoundsIn(area, second).Left, 3);
            Assert.True(BoundsIn(area, second).Top > BoundsIn(area, first).Bottom - 0.5);
            Assert.True(BoundsIn(area, prose).Top > BoundsIn(area, second).Bottom);

            // Nothing about it is in a card frame, and the words are the caption's dim text, not the answer's white.
            Assert.False(IsInside(first, transcript, element => element is ContentControl { Content: MessageCard }));
            Assert.DoesNotContain(Descendants<ContentControl>(transcript), control => control.Content is MessageCard);
            Assert.NotEqual(((SolidColorBrush)prose.Foreground).Color, ((SolidColorBrush)first.Foreground).Color);
        }
        finally { panel.Close(); }
    }));
}
