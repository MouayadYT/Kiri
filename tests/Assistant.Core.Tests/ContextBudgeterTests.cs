using Assistant.Core.Budgeting;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Settings;
using Xunit;
using static Assistant.Core.Tests.BudgetTestData;

namespace Assistant.Core.Tests;

/// <summary>
/// The rules that fit a conversation into the model's window: what has priority, what is left out or cut short, and
/// what the user is told. Sizes are exact: a text made by <see cref="BudgetTestData.Tokens"/> is as many tokens as it has
/// words, a message costs its text and <see cref="ContextBudgeter.MessageOverheadTokens"/>, and the request
/// <see cref="ContextBudgeter.RequestOverheadTokens"/> and its instructions (4 tokens here).
/// </summary>
public sealed class ContextBudgeterTests
{
    private static readonly string Instructions = Sized("rules", 4);
    private static readonly int Fixed = ContextBudgeter.RequestOverheadTokens + 4;
    private static readonly int Overhead = ContextBudgeter.MessageOverheadTokens;
    private static readonly int Block = ContextBudgeter.ContextBlockOverheadTokens;
    private static readonly int CutMarker = Estimate(ContextBudgeter.ContextCutMarker);

    // A turn of the conversations below: a question and an answer of 20 tokens each.
    private static readonly int TurnCost = 2 * (ContextBudgeter.MessageOverheadTokens + 20);

    private readonly ContextBudgeter _budgeter = new(new HeuristicTokenEstimator());

    // ---- Nothing to cut -------------------------------------------------------------------------------------------

    [Fact]
    public void WhenEverythingFits_TheConversationIsReturnedAsItIs_WithEveryTokenAccountedFor()
    {
        var conversation = new[]
        {
            User(Sized("first", 10)), Answer(Sized("reply", 10)), User(Sized("second", 10)),
        };

        var fit = Fit(conversation, room: 1000);

        Assert.Same(conversation, fit.Messages);
        Assert.Empty(fit.Notices);
        Assert.Equal(100, fit.MaxOutputTokens);
        Assert.False(fit.Report.AnythingTrimmed);
        Assert.True(fit.Report.Fits);
        Assert.Equal(Fixed + 3 * (Overhead + 10), fit.Report.EstimatedPromptTokens);
        Assert.Equal(1, fit.Report.TurnsKept);
        Assert.Equal(0, fit.Report.TurnsLeftOut);
    }

    [Fact]
    public void TheAnswersShare_IsTheMostTheModelIsAskedToWrite()
    {
        var conversation = new[] { User("Hi") };

        Assert.Equal(300, Fit(conversation, room: 1000, reserved: 300).MaxOutputTokens);
        Assert.Equal(
            1024,
            _budgeter.Fit(Instructions, conversation, new ModelInfo("m", 8192), new ContextLimitSettings()).MaxOutputTokens);
    }

    [Fact]
    public void TheConversationGiven_IsNeverChanged_WhateverIsCut()
    {
        var conversation = TurnsThenQuestion(6, Sized("now", 10), File("Big.txt", Tokens(3000)));
        var messages = conversation.ToArray();
        var texts = conversation.Select(message => message.Text).ToArray();
        var itemText = conversation[^1].ContextItems[0].Text;

        var fit = Fit(conversation, room: 300);

        Assert.True(fit.Report.AnythingTrimmed);
        Assert.Equal(messages, conversation);
        Assert.Equal(texts, conversation.Select(message => message.Text));
        Assert.Equal(itemText, conversation[^1].ContextItems[0].Text);
        Assert.Single(conversation[^1].ContextItems);
    }

    [Fact]
    public void AConversationThatDoesNotEndWithTheUsersMessage_IsRefused()
    {
        Assert.Throws<ArgumentException>(() => Fit([], room: 100));
        Assert.Throws<ArgumentException>(() => Fit([User("Hi"), Answer("Hello")], room: 100));
    }

    // ---- Earlier turns: newest first, whole turns only -------------------------------------------------------------

    [Fact]
    public void EarlierTurns_AreKeptNewestFirst_AndTheOldestAreLeftOut()
    {
        var conversation = TurnsThenQuestion(6, Sized("now", 10));
        var room = Fixed + Overhead + 10 + 3 * TurnCost;

        var fit = Fit(conversation, room);

        Assert.Equal(conversation.Skip(6), fit.Messages);
        Assert.StartsWith("turn4", fit.Messages[0].Text, StringComparison.Ordinal);
        Assert.Equal(MessageRole.User, fit.Messages[0].Role);
        Assert.Equal(3, fit.Report.TurnsKept);
        Assert.Equal(3, fit.Report.TurnsLeftOut);
        Assert.Equal([ContextBudgetNotices.EarlierMessagesLeftOut], fit.Notices);
        Assert.Equal(room, fit.Report.EstimatedPromptTokens);
        Assert.True(fit.Report.Fits);
    }

    [Fact]
    public void OneTokenLessRoom_LosesTheWholeTurnItTakes_NotPartOfIt()
    {
        var conversation = TurnsThenQuestion(6, Sized("now", 10));

        var fit = Fit(conversation, room: Fixed + Overhead + 10 + 3 * TurnCost - 1);

        Assert.Equal(2, fit.Report.TurnsKept);
        Assert.Equal(5, fit.Messages.Count);
        Assert.StartsWith("turn5", fit.Messages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATurnThatDoesNotFit_EndsTheRun_SoNoOlderTurnSkipsAheadOfIt()
    {
        var small1 = new[] { User(Sized("small1", 10)), Answer(Sized("reply1", 10)) };
        var huge = new[] { User(Sized("huge", 20)), Answer(Sized("hugeanswer", 500)) };
        var small3 = new[] { User(Sized("small3", 10)), Answer(Sized("reply3", 10)) };
        var conversation = small1.Concat(huge).Concat(small3).Append(User(Sized("now", 10))).ToArray();

        // Room for the last small turn with plenty to spare: the first small turn would fit in it, but is not kept.
        var fit = Fit(conversation, room: Fixed + (Overhead + 10) + 2 * (Overhead + 10) + 50);

        Assert.Equal(small3.Append(conversation[^1]), fit.Messages);
        Assert.Equal(1, fit.Report.TurnsKept);
        Assert.Equal(2, fit.Report.TurnsLeftOut);
    }

    [Fact]
    public void AQuestionThatWasNeverAnswered_StaysWithTheTurnItBelongsTo()
    {
        var conversation = new[]
        {
            User(Sized("unanswered", 10)), User(Sized("second", 10)), Answer(Sized("reply", 10)), User(Sized("now", 10)),
        };
        var room = Fixed + Overhead + 10 + 3 * (Overhead + 10);

        var kept = Fit(conversation, room);
        var oneLess = Fit(conversation, room - 1);

        Assert.Equal(conversation, kept.Messages);
        Assert.Equal(1, kept.Report.TurnsKept);

        // Not the second question and its answer alone: the two questions were joined into one message.
        Assert.Equal([conversation[3]], oneLess.Messages);
        Assert.Equal(1, oneLess.Report.TurnsLeftOut);
    }

    [Fact]
    public void AnAnswerThatSaidNothing_CostsNothing_BecauseThePromptDoesNotSendIt()
    {
        var conversation = new[] { User(Sized("first", 10)), Answer(string.Empty), User(Sized("second", 10)) };

        var fit = Fit(conversation, room: 1000);

        Assert.Equal(Fixed + 2 * (Overhead + 10), fit.Report.EstimatedPromptTokens);
    }

    [Fact]
    public void AnAnswerThatCalledATool_CostsItsCallToo()
    {
        var call = new ToolCall("c1", "search_files", "{\"query\":\"report\"}");
        var answer = Answer(string.Empty) with { ToolCalls = [call] };
        var conversation = new[] { User(Sized("first", 10)), answer, User(Sized("second", 10)) };

        var fit = Fit(conversation, room: 1000);

        var callCost = Estimate(call.ToolName) + Estimate(call.ArgumentsJson) + 4;
        Assert.Equal(Fixed + 2 * (Overhead + 10) + Overhead + callCost, fit.Report.EstimatedPromptTokens);
    }

    // ---- The instructions and the user's own message ----------------------------------------------------------------

    [Fact]
    public void TheInstructions_AreNeverCut_AndTakeTheirShareBeforeAnythingElse()
    {
        var conversation = TurnsThenQuestion(6, Sized("now", 10));
        var room = Fixed + Overhead + 10 + 3 * TurnCost;

        var brief = _budgeter.Fit(Instructions, conversation, LargeModel, Room(room));
        var lengthy = _budgeter.Fit(Sized("rules", 60), conversation, LargeModel, Room(room));

        Assert.Equal(3, brief.Report.TurnsKept);
        Assert.Equal(2, lengthy.Report.TurnsKept);
        Assert.True(lengthy.Report.Fits);
        Assert.Equal(
            ContextBudgeter.RequestOverheadTokens + 60 + Overhead + 10 + 2 * TurnCost,
            lengthy.Report.EstimatedPromptTokens);
    }

    [Fact]
    public void AQuestionThatAloneDoesNotFit_LosesItsMiddle_KeepingBothEnds()
    {
        var question = "START " + Tokens(2000) + " END";
        var conversation = new[] { User("Earlier"), Answer("Answer"), User(question) };

        var fit = Fit(conversation, room: 300);

        var text = fit.Messages[^1].Text;
        Assert.StartsWith("START", text, StringComparison.Ordinal);
        Assert.EndsWith("END", text, StringComparison.Ordinal);
        Assert.Contains(ContextBudgeter.MessageCutMarker, text, StringComparison.Ordinal);
        Assert.True(Estimate(text) + Overhead <= 300 - Fixed);
        Assert.True(fit.Report.QuestionShortened);
        Assert.True(fit.Report.Fits);

        // Nothing is left for the earlier turn, and the user is told about both.
        Assert.Equal([fit.Messages[^1]], fit.Messages);
        Assert.Equal([ContextBudgetNotices.EarlierMessagesLeftOut, ContextBudgetNotices.MessageShortened], fit.Notices);
        Assert.Equal(question, conversation[^1].Text);
    }

    [Fact]
    public void AQuestionThatFits_IsNeverCut()
    {
        var question = "START " + Tokens(200) + " END";

        var fit = Fit([User(question)], room: 300);

        Assert.Equal(question, fit.Messages[^1].Text);
        Assert.False(fit.Report.QuestionShortened);
    }

    [Fact]
    public void WhenTheInstructionsLeaveNoRoom_AShortQuestionIsStillKeptWhole_AndTheReportSaysItDoesNotFit()
    {
        var question = Sized("q", 50);
        var conversation = new[]
        {
            User(Sized("earlier", 10)), Answer(Sized("reply", 10)), User(question, File("Doc", Tokens(100))),
        };

        var fit = _budgeter.Fit(Sized("rules", 400), conversation, LargeModel, Room(100, reserved: 20));

        Assert.False(fit.Report.Fits);
        Assert.Equal([question], fit.Messages.Select(message => message.Text));
        Assert.Empty(fit.Messages[0].ContextItems);
        Assert.Equal(1, fit.Report.TurnsLeftOut);
        Assert.Equal(1, fit.Report.ContextItemsLeftOut);
        Assert.False(fit.Report.QuestionShortened);
    }

    // ---- Context: attached before retrieved, newest first, shared fairly --------------------------------------------

    [Fact]
    public void EarlierTurns_AreServedBeforeAttachedContext_EvenIfThatLeavesLittleForIt()
    {
        var file = File("Notes.docx", Paragraphs(5, 40));
        var conversation = new[] { User(Sized("first", 20)), Answer(Sized("reply", 20)), User(Sized("now", 10), file) };
        var others = Fixed + Overhead + 10 + TurnCost;

        var squeezed = Fit(conversation, room: others + 140);
        var starved = Fit(conversation, room: others + 6);

        Assert.Equal(1, squeezed.Report.TurnsKept);
        Assert.Equal(1, squeezed.Report.ContextItemsShortened);
        Assert.True(squeezed.Report.Fits);
        Assert.Equal(1, starved.Report.TurnsKept);
        Assert.Equal(1, starved.Report.ContextItemsLeftOut);
        Assert.Empty(starved.Messages[^1].ContextItems);
    }

    [Fact]
    public void AttachedContext_IsServedBeforeRetrievedPassages_WhateverTheirOrder()
    {
        var retrieved = Retrieved("Search results", Tokens(500));
        var selection = Selection("Notes", Tokens(100));
        var conversation = new[] { User(Sized("now", 10), retrieved, selection) };

        var fit = Fit(conversation, room: Fixed + Overhead + 10 + 300);

        var items = fit.Messages[^1].ContextItems;
        Assert.Equal(["Search results", "Notes"], items.Select(item => item.DisplayName));
        Assert.Same(selection, items[1]);
        Assert.True(items[0].Text!.Length < retrieved.Text!.Length);
        Assert.EndsWith(ContextBudgeter.ContextCutMarker, items[0].Text, StringComparison.Ordinal);
        Assert.Equal([ContextBudgetNotices.ContextShortened(["Search results"])], fit.Notices);
        Assert.True(fit.Report.Fits);
    }

    [Fact]
    public void TheNewestMessagesContext_IsServedBeforeAnEarlierMessagesContext()
    {
        var oldFile = File("Old.txt", Tokens(300));
        var newFile = File("New.txt", Tokens(300));
        var conversation = new[]
        {
            User(Sized("first", 10), oldFile), Answer(Sized("reply", 10)), User(Sized("now", 10), newFile),
        };
        var others = Fixed + (Overhead + 10) + 2 * (Overhead + 10);

        var fit = Fit(conversation, room: others + (Block + Estimate("New.txt") + 300) + 50);

        Assert.Same(newFile, fit.Messages[^1].ContextItems.Single());
        Assert.Empty(fit.Messages[0].ContextItems);
        Assert.Equal([ContextBudgetNotices.ContextLeftOut(["Old.txt"])], fit.Notices);
        Assert.Equal(1, fit.Report.ContextItemsKept);
        Assert.Equal(1, fit.Report.ContextItemsLeftOut);
    }

    [Fact]
    public void LongContext_IsCutToItsStart_AtAParagraphEnd_AndEndsWithAMarker()
    {
        var original = Paragraphs(50, 20);
        var conversation = new[] { User(Sized("now", 10), File("Report", original)) };

        var fit = Fit(conversation, room: Fixed + Overhead + 10 + 300);

        var kept = fit.Messages[^1].ContextItems.Single().Text!;
        Assert.EndsWith(ContextBudgeter.ContextCutMarker, kept, StringComparison.Ordinal);
        var prefix = kept[..^ContextBudgeter.ContextCutMarker.Length];
        Assert.StartsWith(prefix, original, StringComparison.Ordinal);
        Assert.StartsWith("\n\n", original[prefix.Length..], StringComparison.Ordinal);
        Assert.EndsWith("word", prefix, StringComparison.Ordinal);

        // What the item was allowed, less at most the paragraph the cut moved back over.
        var allowed = 300 - (Block + Estimate("Report")) - CutMarker;
        Assert.InRange(Estimate(prefix), allowed - 21, allowed);
        Assert.Equal([ContextBudgetNotices.ContextShortened(["Report"])], fit.Notices);
        Assert.True(fit.Report.Fits);
    }

    [Fact]
    public void ASmallItem_IsSentWhole_AndALargeOneTakesWhatIsLeft()
    {
        var big = File("Big", Tokens(2000));
        var small = File("Small", Tokens(100));
        var conversation = new[] { User(Sized("compare", 10), big, small) };

        var fit = Fit(conversation, room: Fixed + Overhead + 10 + 600);

        var items = fit.Messages[^1].ContextItems;
        Assert.Equal(["Big", "Small"], items.Select(item => item.DisplayName));
        Assert.Same(small, items[1]);
        var left = 600 - (Block + Estimate("Small") + 100);
        Assert.InRange(Estimate(items[0].Text!), left - (Block + Estimate("Big")) - 5, left - (Block + Estimate("Big")));
        Assert.Equal(1, fit.Report.ContextItemsKept);
        Assert.Equal(1, fit.Report.ContextItemsShortened);
    }

    [Fact]
    public void TwoLargeItems_SplitWhatIsLeftEqually()
    {
        var conversation = new[]
        {
            User(Sized("compare", 10), File("Big1", Tokens(2000)), File("Big2", Tokens(2000))),
        };

        var fit = Fit(conversation, room: Fixed + Overhead + 10 + 800);

        var sizes = fit.Messages[^1].ContextItems.Select(item => Estimate(item.Text!)).ToArray();
        Assert.Equal(2, sizes.Length);
        Assert.InRange(Math.Abs(sizes[0] - sizes[1]), 0, 6);
        Assert.Equal(2, fit.Report.ContextItemsShortened);
        Assert.True(fit.Report.Fits);
    }

    [Fact]
    public void WhenTooManyItemsWouldEachGetTooLittle_TheLastOnesAreLeftOut()
    {
        var files = Enumerable.Range(0, 10).Select(number => File($"Doc{number}", Tokens(500))).ToArray();
        var conversation = new[] { User(Sized("now", 10), files) };

        var fit = Fit(conversation, room: Fixed + Overhead + 10 + 400);

        var kept = fit.Messages[^1].ContextItems;
        Assert.Equal(["Doc0", "Doc1", "Doc2", "Doc3"], kept.Select(item => item.DisplayName));
        Assert.All(kept, item => Assert.EndsWith(ContextBudgeter.ContextCutMarker, item.Text, StringComparison.Ordinal));
        Assert.Equal(4, fit.Report.ContextItemsShortened);
        Assert.Equal(6, fit.Report.ContextItemsLeftOut);
        Assert.Equal(
            [
                ContextBudgetNotices.ContextShortened(["Doc0", "Doc1", "Doc2", "Doc3"]),
                ContextBudgetNotices.ContextLeftOut(["Doc4", "Doc5", "Doc6", "Doc7", "Doc8", "Doc9"]),
            ],
            fit.Notices);
        Assert.True(fit.Report.Fits);
    }

    [Fact]
    public void ContextWithNothingInIt_IsLeftForThePromptBuilder_WhenOtherContextIsCut()
    {
        var empty = File("Empty.txt", null);
        var conversation = new[] { User(Sized("now", 10), empty, File("Big.txt", Tokens(2000))) };

        var fit = Fit(conversation, room: Fixed + Overhead + 10 + 300);

        var items = fit.Messages[^1].ContextItems;
        Assert.Equal(2, items.Count);
        Assert.Same(empty, items[0]);
        Assert.Equal(1, fit.Report.ContextItemsShortened);
    }

    // ---- Images and screenshots -------------------------------------------------------------------------------------

    [Fact]
    public void AnImageOnAVisionModel_CostsItsFixedShare_AndIsLeftOutWhenThatDoesNotFit()
    {
        var vision = new ModelInfo("vision", 1_000_000) { SupportsVision = true };
        var shot = Screenshot("Shot", "ocr text", [1, 2, 3]);
        var conversation = new[] { User(Sized("look", 10), shot) };
        var room = Fixed + Overhead + 10 + ContextBudgeter.ImageTokens;

        var fits = _budgeter.Fit(Instructions, conversation, vision, Room(room));
        var oneLess = _budgeter.Fit(Instructions, conversation, vision, Room(room - 1));

        Assert.Same(conversation, fits.Messages);
        Assert.Equal(room, fits.Report.EstimatedPromptTokens);
        Assert.Equal(1, fits.Report.ContextItemsKept);
        Assert.Empty(oneLess.Messages[^1].ContextItems);
        Assert.Equal([ContextBudgetNotices.ContextLeftOut(["Shot"])], oneLess.Notices);
        Assert.Equal(1, oneLess.Report.ContextItemsLeftOut);
    }

    [Fact]
    public void AnEarlierMessagesImage_CostsNothing_BecauseThePromptDoesNotSendItAgain()
    {
        var vision = new ModelInfo("vision", 1_000_000) { SupportsVision = true };
        var conversation = new[]
        {
            User(Sized("look", 10), Screenshot("Shot", "ocr text", [1, 2, 3])),
            Answer(Sized("reply", 10)),
            User(Sized("and", 10)),
        };

        var fit = _budgeter.Fit(Instructions, conversation, vision, Room(1000));

        Assert.Same(conversation, fit.Messages);
        Assert.Equal(Fixed + 3 * (Overhead + 10), fit.Report.EstimatedPromptTokens);
        Assert.Equal(0, fit.Report.ContextItemsKept);
    }

    [Fact]
    public void AScreenshotOnATextModel_CostsItsRecognizedText()
    {
        var conversation = new[] { User(Sized("look", 10), Screenshot("Shot", Tokens(100), [1, 2, 3])) };

        var fit = Fit(conversation, room: 1000);

        Assert.Equal(Fixed + Overhead + 10 + Block + Estimate("Shot") + 100, fit.Report.EstimatedPromptTokens);
        Assert.Equal(1, fit.Report.ContextItemsKept);
    }

    // ---- Normal and heavy limits ------------------------------------------------------------------------------------

    [Fact]
    public void AConversationIsHeavy_OnlyWhenAMessageCarriesContextWithSomethingInIt()
    {
        Assert.Equal(ContextBudgetMode.Normal, ContextBudgeter.ModeOf([User("Hi")]));
        Assert.Equal(ContextBudgetMode.Normal, ContextBudgeter.ModeOf([User("Hi", File("Empty", null), File("Blank", "  "))]));
        Assert.Equal(ContextBudgetMode.Heavy, ContextBudgeter.ModeOf([User("Hi", Selection("Notes", "text"))]));
        Assert.Equal(ContextBudgetMode.Heavy, ContextBudgeter.ModeOf([User("Hi", Screenshot("Shot", null, [1]))]));

        // Context from an earlier turn keeps the whole conversation heavy.
        Assert.Equal(
            ContextBudgetMode.Heavy,
            ContextBudgeter.ModeOf([User("Hi", File("Doc", "text")), Answer("Hello"), User("And?")]));
    }

    [Fact]
    public void ChatIsHeldToTheNormalLimit_AndAConversationWithContextToTheHeavyOne()
    {
        var limits = new ContextLimitSettings
        {
            NormalContextTokens = 300,
            HeavyContextTokens = 1000,
            ReservedOutputTokens = 100,
        };
        var chat = TurnsThenQuestion(6, Sized("now", 10));
        var withContext = TurnsThenQuestion(6, Sized("now", 10), Selection("Notes", Tokens(10)));

        var plain = _budgeter.Fit(Instructions, chat, LargeModel, limits);
        var heavy = _budgeter.Fit(Instructions, withContext, LargeModel, limits);

        Assert.Equal(ContextBudgetMode.Normal, plain.Report.Mode);
        Assert.Equal(300, plain.Report.Budget.WindowTokens);
        Assert.Equal(2, plain.Report.TurnsKept);
        Assert.Equal(ContextBudgetMode.Heavy, heavy.Report.Mode);
        Assert.Equal(1000, heavy.Report.Budget.WindowTokens);
        Assert.Equal(6, heavy.Report.TurnsKept);
        Assert.Equal(0, heavy.Report.TurnsLeftOut);
    }

    [Fact]
    public void TheModelsOwnWindow_IsNeverExceeded_WhateverTheLimits()
    {
        var limits = new ContextLimitSettings { NormalContextTokens = 100_000, HeavyContextTokens = 100_000 };
        var conversation = TurnsThenQuestion(40, Sized("now", 10));

        var fit = _budgeter.Fit(Instructions, conversation, new ModelInfo("small", 2048), limits);

        Assert.Equal(2048, fit.Report.Budget.WindowTokens);
        Assert.True(fit.Report.EstimatedPromptTokens <= 2048 - fit.MaxOutputTokens);
        Assert.True(fit.Report.TurnsLeftOut > 0);
    }

    // ---- Privacy and repeatability ----------------------------------------------------------------------------------

    [Fact]
    public void TheNoticesNameItemsByLabel_NeverByContent_AndTheReportHoldsCountsOnly()
    {
        var secret = "PRIVATE-CONTENT " + Tokens(3000);
        var conversation = new[] { User("PRIVATE-QUESTION", File("Label.docx", secret)) };

        var fit = Fit(conversation, room: 300);

        Assert.Contains("Label.docx", Assert.Single(fit.Notices), StringComparison.Ordinal);
        Assert.All(fit.Notices, notice => Assert.DoesNotContain("PRIVATE", notice, StringComparison.Ordinal));
        Assert.DoesNotContain("PRIVATE", fit.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", fit.Report.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameConversation_AlwaysGivesTheSameResult()
    {
        var conversation = TurnsThenQuestion(
            8, "START " + Tokens(500) + " END", File("A.txt", Paragraphs(30, 30)), Retrieved("Found", Tokens(900)));

        var first = Fit(conversation, room: 600);
        var second = Fit(conversation, room: 600);

        Assert.Equal(first.Report, second.Report);
        Assert.Equal(first.Notices, second.Notices);
        Assert.Equal(first.Messages.Select(message => message.Id), second.Messages.Select(message => message.Id));
        Assert.Equal(first.Messages.Select(message => message.Text), second.Messages.Select(message => message.Text));
        Assert.Equal(
            first.Messages.SelectMany(message => message.ContextItems).Select(item => item.Text),
            second.Messages.SelectMany(message => message.ContextItems).Select(item => item.Text));
    }

    private BudgetedConversation Fit(IReadOnlyList<Message> conversation, int room, int reserved = 100) =>
        _budgeter.Fit(Instructions, conversation, LargeModel, Room(room, reserved));

    // Turns of a 20-token question and a 20-token answer, then the question to answer.
    private static Message[] TurnsThenQuestion(int turns, string question, params ContextItem[] questionContext)
    {
        var messages = new List<Message>();
        for (var turn = 1; turn <= turns; turn++)
        {
            messages.Add(User(Sized($"turn{turn}", 20)));
            messages.Add(Answer(Sized($"answer{turn}", 20)));
        }

        messages.Add(User(question, questionContext));
        return [.. messages];
    }
}
