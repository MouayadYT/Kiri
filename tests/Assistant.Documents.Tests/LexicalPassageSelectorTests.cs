using Assistant.Core.Contracts;
using Assistant.Core.Documents;
using Assistant.Documents.Context;
using Xunit;

namespace Assistant.Documents.Tests;

/// <summary>
/// Choosing the passages a question needs by the words they share with it (PROJECT_SPEC §4.7): keyword ranking that is
/// deterministic, fits the limits, and comes back in the order of the document.
/// </summary>
public sealed class LexicalPassageSelectorTests
{
    private static readonly LexicalPassageSelector Selector = new();

    private static DocumentPassage Passage(int index, string text, DocumentLocation? location = null, int overlap = 0) => new()
    {
        Index = index,
        Text = text,
        Location = location ?? DocumentLocation.ForPage(index + 1),
        OverlapLength = overlap,
    };

    // A document of passages that say nothing in common, so that only the words put into one make it match.
    private static List<DocumentPassage> Document(int count, Func<int, string?>? special = null)
    {
        var words = new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet", "kilo", "lima" };
        return [.. Enumerable.Range(0, count).Select(index =>
            Passage(index, special?.Invoke(index) ?? $"Passage about {words[index % words.Length]} and unrelated matters number {index} here."))];
    }

    private static PassageSelectionOptions Limits(int maxPassages = 100, int maxCharacters = 100) =>
        new() { MaxPassages = maxPassages, MaxCharacters = maxCharacters };

    private static int[] Indexes(PassageSelection selection) => [.. selection.Passages.Select(selected => selected.Passage.Index)];

    // -- Whole, or chosen. --

    [Fact]
    public void ADocumentThatFitsTheLimitsIsTakenWholeWhateverWasAsked()
    {
        var passages = Document(3);

        var selection = Selector.Select(passages, "something entirely different about zebras");

        Assert.Equal(PassageSelectionReason.WholeDocument, selection.Reason);
        Assert.Equal([0, 1, 2], Indexes(selection));
        Assert.True(selection.IsComplete);
        Assert.Equal(3, selection.TotalPassages);
    }

    [Fact]
    public void ADocumentThatHasTooManyPassagesOrTooManyCharactersIsNotTakenWhole()
    {
        var passages = Document(6);

        Assert.Equal(PassageSelectionReason.NoQueryTerms, Selector.Select(passages, "what is this", Limits(maxPassages: 5, maxCharacters: 100_000)).Reason);
        Assert.Equal(PassageSelectionReason.NoQueryTerms, Selector.Select(passages, "what is this", Limits(maxPassages: 10, maxCharacters: 100)).Reason);
        Assert.Equal(PassageSelectionReason.WholeDocument, Selector.Select(passages, "what is this", Limits(maxPassages: 6, maxCharacters: 100_000)).Reason);
    }

    [Fact]
    public void OverlapIsCountedOnceWhenAskingWhetherTheDocumentFits()
    {
        var first = Passage(0, new string('a', 100));
        var second = Passage(1, new string('a', 30) + new string('b', 70), overlap: 30);

        Assert.Equal(PassageSelectionReason.WholeDocument, Selector.Select([first, second], "x", Limits(maxCharacters: 170)).Reason);
        Assert.NotEqual(PassageSelectionReason.WholeDocument, Selector.Select([first, second], "x", Limits(maxCharacters: 169)).Reason);
    }

    [Fact]
    public void PassagesThatHoldTheWordsOfTheQuestionAreSelectedInTheOrderOfTheDocument()
    {
        var passages = Document(12, index => index switch
        {
            9 => "The budget for the year was approved in the spring.",
            3 => "Next year the budget will grow, the board said, by ten percent.",
            _ => null,
        });

        var selection = Selector.Select(passages, "What is the budget?", Limits(maxPassages: 2, maxCharacters: 400));

        Assert.Equal(PassageSelectionReason.Matched, selection.Reason);
        Assert.Equal([3, 9], Indexes(selection));
        Assert.All(selection.Passages, selected => Assert.True(selected.Score > 0));
        Assert.Equal(12, selection.TotalPassages);
        Assert.False(selection.IsComplete);
        Assert.Equal(1, selection.QueryTermCount);
    }

    [Fact]
    public void OnlyPassagesThatMatchAreSelectedEvenWhenThereIsRoomForMore()
    {
        var passages = Document(12, index => index == 5 ? "The budget for the year." : null);

        var selection = Selector.Select(passages, "budget", Limits(maxPassages: 8));

        Assert.Equal([5], Indexes(selection));
    }

    [Fact]
    public void APassageThatOnlyHoldsTheWordsMostPassagesHoldIsNotSelected()
    {
        var passages = Document(9, index => index == 4
            ? "Guests should treat the workshop with care."
            : "Guests should treat the room with care.");

        var selection = Selector.Select(passages, "how should guests treat the workshop?", Limits(maxPassages: 5, maxCharacters: 1_000));

        Assert.Equal(PassageSelectionReason.Matched, selection.Reason);
        Assert.Equal([4], Indexes(selection));
    }

    [Fact]
    public void WhenEveryPassageHoldsTheWordsOfTheQuestionTheBestAreSelectedUpToTheLimit()
    {
        var passages = Document(9, _ => "Guests should treat the room with care.");

        var selection = Selector.Select(passages, "guests treat", Limits(maxPassages: 3, maxCharacters: 1_000));

        Assert.Equal([0, 1, 2], Indexes(selection));
    }

    // -- How passages rank. --

    [Fact]
    public void ARarerWordCountsForMoreThanACommonOne()
    {
        var passages = Document(8, index => index switch
        {
            0 => "common common common common common",
            1 => "common rare",
            _ => "common filler text here",
        });

        var selection = Selector.Select(passages, "common rare", Limits(maxPassages: 1));

        Assert.Equal([1], Indexes(selection));
    }

    [Fact]
    public void AShortPassageOutranksALongOneThatSaysTheSame()
    {
        var passages = Document(8, index => index switch
        {
            2 => "The budget " + string.Join(' ', Enumerable.Repeat("and more words that fill the passage", 12)),
            5 => "The budget plan.",
            _ => null,
        });

        var selection = Selector.Select(passages, "budget", Limits(maxPassages: 1));

        Assert.Equal([5], Indexes(selection));
    }

    [Fact]
    public void WordsOfTheQuestionThatStandTogetherInAPassageEarnMore()
    {
        var passages = Document(8, index => index switch
        {
            1 => "The team marketing plan for spring.",
            6 => "The marketing team plan for spring.",
            _ => null,
        });

        var selection = Selector.Select(passages, "marketing team plan", Limits(maxPassages: 1));

        Assert.Equal([6], Indexes(selection));
    }

    [Fact]
    public void TheHeadingOrTitleOfAPassageCountsAsPartOfIt()
    {
        var passages = Document(8);
        passages[4] = passages[4] with { Location = DocumentLocation.ForSection(5, "Install > Windows") };

        var selection = Selector.Select(passages, "windows", Limits(maxPassages: 1));

        Assert.Equal(PassageSelectionReason.Matched, selection.Reason);
        Assert.Equal([4], Indexes(selection));
    }

    [Theory]
    [InlineData("budgets", "The budget was approved.")]
    [InlineData("budget", "Several budgets were approved.")]
    [InlineData("meetings", "We had a meeting on Monday.")]
    [InlineData("meeting", "Two meetings were held.")]
    [InlineData("companies", "The company grew.")]
    [InlineData("REPORTED", "The report said so.")]
    [InlineData("making", "They make it by hand.")]
    [InlineData("boxes", "A box of tools.")]
    public void FormsOfAWordMeet(string question, string sentence)
    {
        var passages = Document(8, index => index == 3 ? sentence : null);

        var selection = Selector.Select(passages, question, Limits(maxPassages: 1));

        Assert.Equal(PassageSelectionReason.Matched, selection.Reason);
        Assert.Equal([3], Indexes(selection));
    }

    [Fact]
    public void CaseAccentsAndApostrophesDoNotMatter()
    {
        var passages = Document(8, index => index switch
        {
            1 => "My résumé is attached.",
            4 => "Anna’s archive holds it.",
            _ => null,
        });

        Assert.Equal([1], Indexes(Selector.Select(passages, "RESUME", Limits(maxPassages: 1))));
        Assert.Equal([1], Indexes(Selector.Select(passages, "résumé", Limits(maxPassages: 1))));
        Assert.Equal([4], Indexes(Selector.Select(passages, "annas archive", Limits(maxPassages: 1))));
        Assert.Equal([4], Indexes(Selector.Select(passages, "Anna's archive", Limits(maxPassages: 1))));
    }

    [Fact]
    public void WordsOfAScriptWithoutSpacesAreFoundByPairsOfNeighbours()
    {
        var passages = Document(8, index => index == 2 ? "今年的预算已经批准，会议在星期一举行。" : null);

        var selection = Selector.Select(passages, "预算是多少", Limits(maxPassages: 1));

        Assert.Equal(PassageSelectionReason.Matched, selection.Reason);
        Assert.Equal([2], Indexes(selection));
    }

    // -- A question with nothing to look for, or nothing found. --

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("What is this about?")]
    [InlineData("tell me about the document please")]
    [InlineData("Summarize this document")]
    [InlineData("please explain the attached file")]
    [InlineData("can you describe it and give me an overview?")]
    [InlineData("yes, summarize its content")]
    [InlineData("read the document and summarize it")]
    [InlineData("give me the main points and a quick summary")]
    [InlineData("ok, go through it and tell me the key takeaways")]
    public void AQuestionWithNoWordWorthLookingForIsAboutTheWholeDocument_SoItTakesPassagesFromTheStartToTheEnd(string question)
    {
        var passages = Document(10);

        // Room for two passages: the first and the last, not the first two.
        var selection = Selector.Select(passages, question, Limits(maxCharacters: 130));

        Assert.Equal(PassageSelectionReason.NoQueryTerms, selection.Reason);
        Assert.Equal([0, 9], Indexes(selection));
        Assert.Equal(0, selection.QueryTermCount);
    }

    [Fact]
    public void ThePassagesOfAnOverviewAreSpreadEvenly_AsManyAsTheLimitsAllow_InTheOrderOfTheDocument()
    {
        var passages = Document(33);

        var four = Selector.Select(passages, "What is this about?", Limits(maxPassages: 4, maxCharacters: 100_000));
        var nine = Selector.Select(passages, "What is this about?", Limits(maxPassages: 9, maxCharacters: 100_000));

        Assert.Equal([0, 11, 21, 32], Indexes(four));
        Assert.Equal([0, 4, 8, 12, 16, 20, 24, 28, 32], Indexes(nine));
        Assert.Equal(33, nine.TotalPassages);
        Assert.False(nine.IsComplete);

        // The same document and limits give the same passages.
        Assert.Equal(Indexes(nine), Indexes(Selector.Select(passages, "Summarize it", Limits(maxPassages: 9, maxCharacters: 100_000))));
    }

    [Fact]
    public void AnOverviewGivesUpPassagesUntilTheRestFit_AndTakesTheStartWhenOnlyOneDoes()
    {
        var passages = Document(12);
        var one = passages[0].Text.Length;

        var three = Selector.Select(passages, "Summarize it", Limits(maxPassages: 8, maxCharacters: one * 3 + 20));
        Assert.Equal(3, three.Passages.Count);
        Assert.Equal(0, Indexes(three)[0]);
        Assert.Equal(11, Indexes(three)[^1]);

        var tiny = Selector.Select(passages, "Summarize it", Limits(maxPassages: 8, maxCharacters: one + 5));
        Assert.Equal([0], Indexes(tiny));
    }

    [Fact]
    public void WordsThatNoPassageHoldTakeTheStartOfTheDocumentToo()
    {
        var passages = Document(10);

        var selection = Selector.Select(passages, "zebras and giraffes", Limits(maxCharacters: 130));

        Assert.Equal(PassageSelectionReason.NoMatch, selection.Reason);
        Assert.Equal([0, 1], Indexes(selection));
        Assert.Equal(2, selection.QueryTermCount);
        Assert.All(selection.Passages, selected => Assert.Equal(0, selected.Score));
    }

    // -- Places the question names. --

    [Fact]
    public void APlaceTheQuestionNamesComesFirstWhateverItsWords()
    {
        var passages = Document(12);

        var page = Selector.Select(passages, "What does page 3 say?", Limits(maxPassages: 1));
        var range = Selector.Select(passages, "summarize pages 4-5", Limits(maxPassages: 2, maxCharacters: 400));

        Assert.Equal(PassageSelectionReason.Matched, page.Reason);
        Assert.Equal([2], Indexes(page));
        Assert.Equal([3, 4], Indexes(range));
    }

    [Fact]
    public void ASlideAndItsNotesAreTheSamePlace_AndAGatheredPassageIsAtEachOfItsSlides()
    {
        var passages = new List<DocumentPassage>
        {
            Passage(0, "Title slide.", DocumentLocation.ForSlide(1, "Plan")),
            Passage(1, "Figures for the year.", DocumentLocation.ForSlide(2)),
            Passage(2, "Say that costs fell.", DocumentLocation.ForSlideNotes(2)),
            Passage(3, "Slides three to five.", DocumentLocation.ForSlide(3)) with { EndLocation = DocumentLocation.ForSlide(5) },
            Passage(4, "Closing.", DocumentLocation.ForSlide(6)),
        };

        Assert.Equal([1, 2], Indexes(Selector.Select(passages, "what is on slide 2", Limits(maxPassages: 3, maxCharacters: 50))));
        Assert.Equal([3], Indexes(Selector.Select(passages, "what is on slide 4", Limits(maxPassages: 1, maxCharacters: 50))));
    }

    [Fact]
    public void APlaceAndWordsTogetherRankThePlaceFirstThenTheWords()
    {
        var passages = Document(12, index => index == 10 ? "The budget again." : null);

        var selection = Selector.Select(passages, "the budget on page 2", Limits(maxPassages: 2));

        Assert.Equal([1, 10], Indexes(selection));
        Assert.True(selection.Passages[0].Score > selection.Passages[1].Score);
    }

    [Fact]
    public void APlaceThatTheDocumentDoesNotHaveMatchesNothing()
    {
        var selection = Selector.Select(Document(12), "what is on page 99", Limits(maxPassages: 1));

        Assert.Equal(PassageSelectionReason.NoMatch, selection.Reason);
        Assert.Equal([0], Indexes(selection));
    }

    // -- The limits. --

    [Fact]
    public void APassageThatDoesNotFitIsSkippedAndAShorterOneTakesItsPlace()
    {
        var passages = Document(8, index => index switch
        {
            1 => "budget " + new string('x', 150),
            5 => "budget plan",
            _ => null,
        });

        var selection = Selector.Select(passages, "budget", Limits(maxCharacters: 100));

        Assert.Equal([5], Indexes(selection));
    }

    [Fact]
    public void APassageLongerThanTheLimitAloneIsCutToFit()
    {
        var text = string.Join(' ', Enumerable.Range(0, 60).Select(number => $"budget{number}"));
        var passages = Document(8, index => index == 2 ? text : null);

        var selection = Selector.Select(passages, "budget0", Limits(maxCharacters: 100));

        var only = Assert.Single(selection.Passages).Passage;
        Assert.InRange(only.Text.Length, 1, 100);
        Assert.StartsWith(only.Text, text, StringComparison.Ordinal);
        Assert.Equal(2, only.Index);

        // The start of a document whose first passage is too long is cut the same way.
        var start = Selector.Select(passages, "zebra", Limits(maxCharacters: 30));
        Assert.Equal(PassageSelectionReason.NoMatch, start.Reason);
        Assert.InRange(Assert.Single(start.Passages).Passage.Text.Length, 1, 30);
    }

    [Fact]
    public void PassagesThatFollowEachOtherAreCountedOnceWhereTheyOverlap()
    {
        var first = Passage(0, "budget " + new string('a', 93));
        var second = Passage(1, new string('a', 30) + " budget " + new string('b', 62), overlap: 30);
        var passages = new List<DocumentPassage> { first, second };
        passages.AddRange(Enumerable.Range(2, 6).Select(index => Passage(index, $"unrelated filler number {index}")));

        var together = Selector.Select(passages, "budget", Limits(maxCharacters: 170));
        var apart = Selector.Select(passages, "budget", Limits(maxCharacters: 169));

        Assert.Equal([0, 1], Indexes(together));
        Assert.Equal(170, together.CharacterCount);
        Assert.Single(apart.Passages);
    }

    // -- The same every time. --

    [Fact]
    public void EqualScoresAreToldApartByTheOrderOfTheDocument_AndTheSelectionIsTheSameEveryTime()
    {
        var passages = Document(10, index => index is 2 or 5 or 8 ? "The budget is here." : null);

        var first = Selector.Select(passages, "budget", Limits(maxPassages: 2));
        var second = new LexicalPassageSelector().Select(passages, "budget", Limits(maxPassages: 2));

        Assert.Equal([2, 5], Indexes(first));
        Assert.Equal(first.Passages, second.Passages);
    }

    [Fact]
    public void ALongQuestionIsReadAsFarAsTheMostTerms()
    {
        var question = string.Join(' ', Enumerable.Range(0, 100).Select(number => $"word{number}x"));

        var selection = Selector.Select(Document(10), question, Limits(maxPassages: 1));

        Assert.Equal(QuestionTerms.MaxTerms, selection.QueryTermCount);
    }

    [Fact]
    public void NoPassagesIsAnEmptySelection_AndNoQuestionIsRefused()
    {
        Assert.Empty(Selector.Select([], "anything").Passages);
        Assert.Throws<ArgumentNullException>(() => Selector.Select(Document(2), null!));
        Assert.Throws<ArgumentNullException>(() => Selector.Select(null!, "x"));
    }

    [Fact]
    public void TheBuilderSelectsWithTheSelectorItIsGiven()
    {
        var chosen = new List<string>();
        var builder = new DocumentContextBuilder(new RecordingSelector(chosen));
        var context = builder.Build("Some text to choose from.");

        var selection = builder.Select(context, "the question");

        Assert.Equal(["the question"], chosen);
        Assert.Equal(PassageSelection.Empty, selection);
    }

    private sealed class RecordingSelector(List<string> questions) : IPassageSelector
    {
        public PassageSelection Select(IReadOnlyList<DocumentPassage> passages, string question, PassageSelectionOptions? options = null)
        {
            questions.Add(question);
            return PassageSelection.Empty;
        }
    }
}
