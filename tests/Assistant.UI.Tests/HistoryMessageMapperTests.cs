using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assistant.Core.Domain;
using Assistant.UI.History;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// What is kept of a message when it is saved to the history, and what it comes back as: its text, place, time, outcome,
/// context descriptors and structured cards, and nothing that was captured.
/// </summary>
public sealed class HistoryMessageMapperTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 30, 9, 30, 0, TimeSpan.Zero);

    private readonly RecordingClipboard _clipboard = new();

    private MessageMapper Mapper => new(_clipboard);

    private MessageViewModel RoundTrip(MessageViewModel message) => Mapper.ToViewModel(Mapper.ToDomain(message));

    private static MessageViewModel Answer(params MessageContent[] parts)
    {
        var answer = new MessageViewModel(MessageRole.Assistant) { CreatedAt = At };
        foreach (var part in parts)
        {
            answer.Content.Add(part);
        }

        return answer;
    }

    private static string[] Kinds(Message message) => [.. message.Cards.Select(card => card.Kind)];

    [Fact]
    public void AUsersMessageKeepsItsTextIdentityAndTime()
    {
        var asked = new MessageViewModel(MessageRole.User, "  What is 9+10?\nPlease.  ") { CreatedAt = At };

        var saved = Mapper.ToDomain(asked);

        Assert.Equal(asked.Id, saved.Id);
        Assert.Equal(MessageRole.User, saved.Role);
        Assert.Equal("  What is 9+10?\nPlease.  ", saved.Text);
        Assert.Equal(At, saved.CreatedAt);
        Assert.Empty(saved.Cards);
        Assert.Equal(MessageOutcome.Complete, saved.Outcome);
    }

    [Fact]
    public void AnImageFileAttachedToAMessageIsKeptAsItsPath_ButAnImageInMemoryIsNot()
    {
        OnSta.Run(() =>
        {
            var pixels = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            var asked = new MessageViewModel(
                MessageRole.User,
                "What is this?",
                [new ImageItem("sign.jpg", @"C:\Pictures\sign.jpg"), new ImageItem("clipboard", pixels)]);

            var saved = Mapper.ToDomain(asked);

            var item = Assert.Single(saved.ContextItems);
            Assert.Equal(ContextItemType.Image, item.Type);
            Assert.Equal("sign.jpg", item.DisplayName);
            Assert.Equal(@"C:\Pictures\sign.jpg", item.FilePath);
            Assert.Null(item.Text);
            Assert.True(item.ImageData.IsEmpty);
        });
    }

    [Fact]
    public void AnAttachedImageComesBackAsAnImageThatIsReadFromItsFileAgain()
    {
        var asked = new MessageViewModel(MessageRole.User, "Where?", [new ImageItem("valley.jpg", @"C:\p\valley.jpg")]);

        var back = RoundTrip(asked);

        var image = Assert.Single(back.Attachments);
        Assert.Equal("valley.jpg", image.Name);
        Assert.Equal(@"C:\p\valley.jpg", image.Path);
        Assert.Equal(asked.Id, back.Id);
    }

    [Fact]
    public void ASavedImageWithoutANameIsNamedAfterItsFile_AndOnlyImageFilesComeBackAsAttachments()
    {
        var saved = new Message(Guid.NewGuid(), MessageRole.User, "Compare these", At)
        {
            ContextItems =
            [
                new ContextItem(Guid.NewGuid(), ContextItemType.Image, " ") { FilePath = @"C:\p\harbor.png" },
                new ContextItem(Guid.NewGuid(), ContextItemType.File, "notes.txt") { FilePath = @"C:\p\notes.txt" },
                new ContextItem(Guid.NewGuid(), ContextItemType.Image, "pasted"),
            ],
        };

        var shown = Mapper.ToViewModel(saved);

        var image = Assert.Single(shown.Attachments);
        Assert.Equal("harbor.png", image.Name);
        Assert.Equal(@"C:\p\harbor.png", image.Path);
    }

    [Fact]
    public async Task AConversationsCardNamesItsImageTheSameWay_AndShowsNoneWithoutAFile()
    {
        var history = new FakeHistoryService();
        history.Summaries.Add(new ConversationSummary(Guid.NewGuid(), "Harbor", At, At, 2)
        {
            LatestImage = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "") { FilePath = @"C:\p\harbor.png" },
        });
        history.Summaries.Add(new ConversationSummary(Guid.NewGuid(), "Pasted", At, At, 2)
        {
            LatestImage = new ContextItem(Guid.NewGuid(), ContextItemType.Image, "pasted"),
        });

        var listed = await new ConversationHistorySource(history, Mapper).ListAsync();

        Assert.Equal("harbor.png", listed[0].Image?.Name);
        Assert.Equal(@"C:\p\harbor.png", listed[0].Image?.Path);
        Assert.Null(listed[1].Image);
    }

    [Fact]
    public void AnAnswersProseIsItsText_AndItsOutcomeIsKept()
    {
        var answer = Answer(new TextContent("First paragraph."), new TextContent("Second one."));

        var saved = Mapper.ToDomain(answer);

        Assert.Equal("First paragraph.\n\nSecond one.", saved.Text);
        Assert.Equal(MessageRole.Assistant, saved.Role);
        Assert.Empty(saved.Cards);
    }

    [Theory]
    [InlineData(MessageStatus.Complete, MessageOutcome.Complete, MessageStatus.Complete)]
    [InlineData(MessageStatus.Stopped, MessageOutcome.Stopped, MessageStatus.Stopped)]
    [InlineData(MessageStatus.Failed, MessageOutcome.Failed, MessageStatus.Failed)]
    [InlineData(MessageStatus.Answering, MessageOutcome.Stopped, MessageStatus.Stopped)]
    public void HowAnAnswerEndedIsKept(MessageStatus shown, MessageOutcome saved, MessageStatus back)
    {
        var answer = Answer(new TextContent("Some words"));
        answer.Status = shown;

        Assert.Equal(saved, Mapper.ToDomain(answer).Outcome);
        Assert.Equal(back, RoundTrip(answer).Status);
    }

    [Fact]
    public void AnAnswerStoppedBeforeItsFirstWordsComesBackAsTheStoppedNoteAlone()
    {
        var stopped = Answer();
        stopped.Status = MessageStatus.Stopped;

        var back = RoundTrip(stopped);

        Assert.Equal(MessageStatus.Stopped, back.Status);
        Assert.Empty(back.Content);
    }

    [Fact]
    public void ACardComesBackBetweenTheParagraphsItWasShownBetween()
    {
        var answer = Answer(
            new TextContent("Here is the result:"),
            new CalculationResult("9 + 10", "19"),
            new TextContent("That is all."));

        var saved = Mapper.ToDomain(answer);
        var back = Mapper.ToViewModel(saved);

        Assert.Equal("Here is the result:\n\nThat is all.", saved.Text);
        var card = Assert.Single(saved.Cards);
        Assert.Equal("calculation_result", card.Kind);
        Assert.Equal("Here is the result:".Length, card.TextOffset);
        Assert.Equal(
            [typeof(TextContent), typeof(CalculationResult), typeof(TextContent)],
            back.Content.Select(part => part.GetType()));
        Assert.Equal("Here is the result:", ((TextContent)back.Content[0]).Text);
        Assert.Equal("That is all.", ((TextContent)back.Content[2]).Text);
    }

    [Fact]
    public void CardsAtTheStartAndAtTheEndStayThere()
    {
        var answer = Answer(
            new RichAnswerCard("Conversion", "5 km", "3.1 mi"),
            new TextContent("Between."),
            new CodeContent("Console.WriteLine(1);", "C#"));

        var saved = Mapper.ToDomain(answer);
        var back = Mapper.ToViewModel(saved);

        Assert.Equal([0, "Between.".Length], saved.Cards.Select(card => card.TextOffset));
        Assert.Equal(
            [typeof(RichAnswerCard), typeof(TextContent), typeof(CodeContent)],
            back.Content.Select(part => part.GetType()));
    }

    [Fact]
    public void SeveralCardsInARowKeepTheirOrder_AndAnAnswerOfCardsAloneHasNoText()
    {
        var answer = Answer(new RichAnswerCard("One", "1"), new RichAnswerCard("Two", "2"), new CodeContent("x", null));

        var saved = Mapper.ToDomain(answer);
        var back = Mapper.ToViewModel(saved);

        Assert.Equal(string.Empty, saved.Text);
        Assert.Equal(["rich_answer_card", "rich_answer_card", "code"], Kinds(saved));
        Assert.Equal([0, 0, 0], saved.Cards.Select(card => card.TextOffset));
        Assert.Equal(["One", "Two"], back.Content.OfType<RichAnswerCard>().Select(card => card.Label));
        Assert.IsType<CodeContent>(back.Content[2]);
    }

    [Fact]
    public void ProseAlongsideCardsKeepsItsOwnBlankLines_AndOnlyTheJoiningOnesGo()
    {
        var answer = Answer(
            new TextContent("Line one\n\nLine two"),
            new CodeContent("code", null),
            new TextContent("After\n\nthe code"));

        var back = RoundTrip(answer);

        Assert.Equal("Line one\n\nLine two", ((TextContent)back.Content[0]).Text);
        Assert.Equal("After\n\nthe code", ((TextContent)back.Content[2]).Text);
        Assert.Equal(answer.Text, back.Text);
    }

    [Fact]
    public void AnAnswersTextIsTheSameAfterAnyNumberOfRoundTrips()
    {
        var answer = Answer(
            new TextContent("A"), new RichAnswerCard("L", "R"), new TextContent("B"), new CodeContent("c", "C#"), new TextContent("C"));

        var once = RoundTrip(answer);
        var twice = RoundTrip(once);

        Assert.Equal(answer.Text, twice.Text);
        Assert.Equal(once.Content.Select(part => part.GetType()), twice.Content.Select(part => part.GetType()));
    }

    [Fact]
    public void ARichCardKeepsItsLabelExpressionResultAndNote_AndItsCopyButtonStillCopies()
    {
        var answer = Answer(new RichAnswerCard("Conversion", "5 km", "3.1 mi", "rounded", null));

        var saved = Mapper.ToDomain(answer);
        var back = Assert.IsType<RichAnswerCard>(Assert.Single(Mapper.ToViewModel(saved).Content));

        Assert.Equal("Conversion", back.Label);
        Assert.Equal("3.1 mi", back.Expression);
        Assert.Equal("5 km", back.Result);
        Assert.Equal("rounded", back.Secondary);
        Assert.NotNull(back.CopyCommand);
        back.CopyCommand.Execute(null);
        Assert.Equal(["5 km"], _clipboard.Copied);
    }

    [Fact]
    public void ACalculationComesBackAsACalculation_WithItsCopyButton()
    {
        var back = Assert.IsType<CalculationResult>(Assert.Single(RoundTrip(Answer(new CalculationResult("9 + 10", "19"))).Content));

        Assert.Equal("9 + 10 =", back.Caption);
        Assert.Equal("19", back.Result);
        back.CopyCommand!.Execute(null);
        Assert.Equal(["19"], _clipboard.Copied);
    }

    [Fact]
    public void ACodeBlockKeepsItsCodeAndLanguage()
    {
        const string code = "static int Add(int a, int b)\n{\n\treturn a + b;\n}";
        var back = Assert.IsType<CodeContent>(Assert.Single(RoundTrip(Answer(new CodeContent(code, "C#"))).Content));

        Assert.Equal(code, back.Code);
        Assert.Equal("C#", back.Language);
        back.CopyCommand!.Execute(null);
        Assert.Equal([code], _clipboard.Copied);
    }

    [Fact]
    public void AGalleryKeepsItsImageFiles_AndLeavesOutImagesThatExistOnlyInMemory()
    {
        OnSta.Run(() =>
        {
            var pixels = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            var answer = Answer(new ImageCollection(
                [new ImageItem("a.jpg", @"C:\p\a.jpg"), new ImageItem("memory", pixels), new ImageItem("b.png", @"C:\p\b.png")]));

            var back = Assert.IsType<ImageCollection>(Assert.Single(RoundTrip(answer).Content));

            Assert.Equal(["a.jpg", "b.png"], back.Images.Select(image => image.Name));
            Assert.Equal([@"C:\p\a.jpg", @"C:\p\b.png"], back.Images.Select(image => image.Path));
        });
    }

    [Fact]
    public void AGalleryOfImagesThatExistOnlyInMemoryIsNotSaved()
    {
        OnSta.Run(() =>
        {
            var pixels = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
            var answer = Answer(new TextContent("Here are your screenshots."), new ImageCollection([new ImageItem("shot", pixels)]));

            var saved = Mapper.ToDomain(answer);

            Assert.Empty(saved.Cards);
            Assert.Equal("Here are your screenshots.", saved.Text);
        });
    }

    [Fact]
    public void AListOfFilesKeepsWhereEachIs_AndNoTextFromAnyOfThem()
    {
        const string snippet = "Groceries 420 · Rent 1,450 — text read out of the file";
        var modified = new DateTimeOffset(2026, 9, 28, 14, 5, 0, TimeSpan.Zero);
        var answer = Answer(new FileCollection(
        [
            new FileItem(SearchResultItemType.File, "Budget 2026.xlsx", @"C:\Docs\Budget 2026.xlsx", modified, snippet),
            new FileItem(SearchResultItemType.Folder, "Finance", @"C:\Docs\Finance"),
        ]));

        var saved = Mapper.ToDomain(answer);
        var back = Assert.IsType<FileCollection>(Assert.Single(Mapper.ToViewModel(saved).Content));

        Assert.DoesNotContain("Groceries", saved.Cards[0].DataJson, StringComparison.Ordinal);
        Assert.DoesNotContain("text read out", saved.Cards[0].DataJson, StringComparison.Ordinal);
        Assert.Equal(["Budget 2026.xlsx", "Finance"], back.Files.Select(file => file.Name));
        Assert.Equal([SearchResultItemType.File, SearchResultItemType.Folder], back.Files.Select(file => file.Kind));
        Assert.Equal([@"C:\Docs\Budget 2026.xlsx", @"C:\Docs\Finance"], back.Files.Select(file => file.Path));
        Assert.Equal(modified, back.Files[0].ModifiedAt);
        Assert.Null(back.Files[0].Snippet);
        Assert.Null(back.Files[1].ModifiedAt);
    }

    [Fact]
    public void CardDataIsPlainJsonThatAPresenterCanReadWithoutTheApp()
    {
        var saved = Mapper.ToDomain(Answer(new CodeContent("x = 1", "Python")));

        using var document = JsonDocument.Parse(saved.Cards[0].DataJson);
        Assert.Equal("x = 1", document.RootElement.GetProperty("code").GetString());
        Assert.Equal("Python", document.RootElement.GetProperty("language").GetString());
    }

    [Fact]
    public void ACardOfAKindThisBuildDoesNotKnow_OrWithDataThatIsNotACard_IsLeftOutAndTheRestStays()
    {
        var saved = new Message(Guid.NewGuid(), MessageRole.Assistant, "First\n\nSecond", At)
        {
            Cards =
            [
                new CardMetadata("hologram", "{}", 5),
                new CardMetadata("code", "{\"language\":\"C#\"}", 5),
                new CardMetadata("rich_answer_card", "\"just a string\"", 5),
                new CardMetadata("image_gallery", "{\"images\":[]}", 5),
                new CardMetadata("file_list", "[1,2,3]", 5),
                new CardMetadata("calculation_result", "{\"expression\":\"\",\"result\":\"1\"}", 5),
            ],
        };

        var back = Mapper.ToViewModel(saved);

        Assert.Equal([typeof(TextContent), typeof(TextContent)], back.Content.Select(part => part.GetType()));
        Assert.Equal("First\n\nSecond", back.Text);
    }

    [Fact]
    public void ACardOffsetOutsideTheTextIsBroughtBackInsideIt()
    {
        var saved = new Message(Guid.NewGuid(), MessageRole.Assistant, "Some words", At)
        {
            Cards = [new CardMetadata("code", "{\"code\":\"x\"}", 400), new CardMetadata("code", "{\"code\":\"y\"}", 2)],
        };

        var back = Mapper.ToViewModel(saved);

        // The second card cannot come before the first, so both follow the text.
        Assert.Equal([typeof(TextContent), typeof(CodeContent), typeof(CodeContent)], back.Content.Select(part => part.GetType()));
    }

    [Fact]
    public void TheMessagesIdAndTimeSurviveTheRoundTrip()
    {
        var answer = Answer(new TextContent("Words"));

        var back = RoundTrip(answer);

        Assert.Equal(answer.Id, back.Id);
        Assert.Equal(At, back.CreatedAt);
        Assert.Equal(MessageRole.Assistant, back.Role);
    }

    [Fact]
    public void AnAnswerWithNothingToKeepIsSavedAsAnEmptyMessage()
    {
        var saved = Mapper.ToDomain(Answer());

        Assert.Equal(string.Empty, saved.Text);
        Assert.Empty(saved.Cards);
    }
}
