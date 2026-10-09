using System.Windows;
using System.Windows.Controls;
using Assistant.Core.Domain;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Attachment chips above the composer (PROJECT_SPEC §4.2) ----------------------------------------------------------------

    private static ImageItem ChipPicture(string name) => new(name, @"C:\Pictures\" + name);

    // -- The chips follow what is attached, in the order it was attached. --

    [Fact]
    public void TheChipsAreOneForEachAttachment_InTheOrderTheyWereAttached_WhateverTheirKind()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        var first = ChipPicture("a.png");
        var text = new TextAttachment("Dear team,\n  the   launch moves to Friday.");
        var document = Attached("Plan.docx");
        var second = ChipPicture("b.png");

        conversation.StartWithAttachment(first);
        Assert.True(conversation.Attach(text));
        Assert.True(conversation.Attach(document));
        Assert.True(conversation.Attach(second));

        Assert.Equal(
            [AttachmentKind.Image, AttachmentKind.Text, AttachmentKind.File, AttachmentKind.Image],
            conversation.Chips.Select(chip => chip.Kind));
        Assert.Equal(
            [(object)first, text, document, second], conversation.Chips.Select(chip => chip.Source), ReferenceComparer.Instance);
        Assert.Equal(["a.png", "Dear team,", "Plan.docx", "b.png"], conversation.Chips.Select(chip => chip.Title));

        // Another document has a chip of its own, at the end; the others stay where they are.
        var other = Attached("Budget.pdf");
        Assert.True(conversation.Attach(other));
        Assert.Equal(
            [(object)first, text, document, second, other], conversation.Chips.Select(chip => chip.Source), ReferenceComparer.Instance);
    }

    [Fact]
    public void AChipsButtonTakesOffWhatItStandsFor_AndSoDoTheAttachmentsThemselves()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        var image = ChipPicture("a.png");
        var text = new TextAttachment("Some words");
        var document = Attached("Plan.docx");
        conversation.StartWithAttachment(image);
        conversation.Attach(text);
        conversation.Attach(document);

        conversation.RemoveAttachmentCommand.Execute(conversation.Chips.Single(chip => chip.Kind == AttachmentKind.Text));
        Assert.Empty(conversation.Texts);
        Assert.Equal(2, conversation.Chips.Count);

        conversation.RemoveAttachmentCommand.Execute(conversation.Chips.Single(chip => chip.Kind == AttachmentKind.File));
        Assert.Null(conversation.Document);
        Assert.Same(image, Assert.Single(conversation.Chips).Source);

        // The image, document or text itself is accepted as well.
        conversation.RemoveAttachmentCommand.Execute(image);
        Assert.Empty(conversation.Chips);
        Assert.Empty(conversation.Attachments);

        // With nothing attached and no message, the composer is gone.
        Assert.False(conversation.CanCompose);
    }

    [Fact]
    public void ATextAttachedOnItsOwnShowsTheComposer_IsNotAttachedTwice_AndGoesOntoTheMessage()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        var text = new TextAttachment("The launch moves to Friday.");

        Assert.True(conversation.Attach(text));
        Assert.False(conversation.Attach(new TextAttachment("  The launch moves to Friday.\n")));
        Assert.True(conversation.CanCompose);
        conversation.Draft = "Summarize this";
        Assert.True(conversation.AskCommand.CanExecute(null));

        Assert.True(conversation.Ask("Summarize this"));
        var question = Assert.Single(conversation.Messages, message => message.Role == MessageRole.User);
        Assert.Same(text, Assert.Single(question.TextAttachments));
        Assert.True(question.HasAttachments);
        Assert.Empty(conversation.Texts);
        Assert.Empty(conversation.Chips);
    }

    [Fact]
    public void EscTakesOffEveryKindOfAttachment_AfterTheDraft()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        conversation.StartWithAttachment(ChipPicture("a.png"));
        conversation.Attach(new TextAttachment("Some words"));
        conversation.Attach(Attached("Plan.docx"));
        conversation.Draft = "typed";

        Assert.False(conversation.HandleEscape());
        Assert.Equal("", conversation.Draft);
        Assert.Equal(3, conversation.Chips.Count);

        Assert.False(conversation.HandleEscape());
        Assert.Empty(conversation.Chips);
        Assert.Empty(conversation.Texts);
        Assert.Null(conversation.Document);
        Assert.True(conversation.HandleEscape());
    }

    [Fact]
    public void StartingANewConversationClearsTheChips()
    {
        var conversation = CreateConversationModel(new FakeMicrophone());
        conversation.StartWithAttachment(ChipPicture("a.png"));
        conversation.Attach(new TextAttachment("Some words"));

        conversation.StartNew("hello");

        Assert.Empty(conversation.Chips);
        Assert.Empty(conversation.Texts);
    }

    [Theory]
    [InlineData("Short note", "Short note")]
    [InlineData("\n\n  Dear   team,\n second line", "Dear team,")]
    public void ATextChipIsNamedByTheTextsFirstWords(string text, string name) =>
        Assert.Equal(name, new TextAttachment(text).Name);

    [Fact]
    public void ALongTextIsNamedByItsFirstWordsCutWithAnEllipsis()
    {
        var name = new TextAttachment(new string('w', 200)).Name;

        Assert.True(name.Length <= 48);
        Assert.EndsWith(((char)0x2026).ToString(), name, StringComparison.Ordinal);
        Assert.Equal("Selected", new TextAttachment("whatever", "Selected").Name);
    }

    [Fact]
    public void AChipSaysWhatItIs_AndWhatItsButtonIsCalled()
    {
        var image = AttachmentChip.For(ChipPicture("shot.png"));
        var file = AttachmentChip.For(Attached("Plan.docx"));
        var text = AttachmentChip.For(new TextAttachment("Some words"));

        Assert.Equal("Remove attachment", image.RemoveName);
        Assert.Equal("Remove attached file", file.RemoveName);
        Assert.Equal("Remove attached text", text.RemoveName);
        Assert.Equal(@"C:\Pictures\shot.png", image.Detail);
        Assert.Equal(@"C:\Docs\Plan.docx", file.Detail);
        Assert.Equal("Text, 10 characters", text.Detail);
        Assert.NotNull(image.Image);
        Assert.Null(file.Image);
    }

    // -- What is attached goes to the model with the question. --

    [Fact]
    public void AnAttachedTextGoesToTheModelWithTheQuestion_AsWhatTheUserSelected() => RunSta(() =>
    {
        var model = new ScriptedModel();
        var answers = DocumentAnswers(model, FilesAllowed());
        var conversation = CreateConversationModel(new FakeMicrophone(), answers);
        conversation.Attach(new TextAttachment("The launch moves to Friday."));

        Assert.True(conversation.Ask("When is the launch?"));
        WaitUntil(() => model.Requests.Count == 1, "The model was not asked.");
        var prompt = model.Requests[0].Messages[^1].Text;
        Assert.StartsWith("<untrusted_context id=\"1\" kind=\"selection\"", prompt, StringComparison.Ordinal);
        Assert.Contains("The launch moves to Friday.", prompt, StringComparison.Ordinal);
        Assert.EndsWith("When is the launch?", prompt, StringComparison.Ordinal);
        model.Write(Assistant.Core.Domain.AssistantResponseChunk.ForTextDelta("Friday."));
        model.End();
        WaitUntil(() => !conversation.IsAnswering, "The answer did not end.");
    });

    // -- The row above the composer. --

    [Fact]
    public void ManyAttachmentsWrapToTwoLinesAtMost_SoTheComposerStaysShort_AndTheChipsScrollBeyondThat() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.StartWithAttachment(ChipPicture("one.png"));
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            var composer = Named<Grid>(panel, "Composer");
            var row = Named<AttachmentChipList>(panel, "ComposerAttachments");
            var oneChipComposer = composer.ActualHeight;
            var oneChipRow = row.ActualHeight;
            Assert.True(oneChipRow is > 28 and < 40, $"A single chip's row is {oneChipRow} tall.");

            // Two short chips share a line.
            model.Attach(ChipPicture("two.png"));
            Pump();
            Assert.Equal(oneChipRow, row.ActualHeight, 0.5);

            for (var picture = 3; picture <= 12; picture++)
            {
                model.Attach(ChipPicture($"picture number {picture} with a long name.png"));
            }

            model.Attach(new TextAttachment("A selection of several words that is long enough to be cut off at the end of the chip"));
            model.Attach(Attached("Quarterly budget review with a very long name that cannot fit the composer at all.pdf"));
            Pump();

            // Fourteen chips take two lines, no more, so the composer grew by one chip's height only.
            Assert.Equal(14, row.Items.Count);
            Assert.Equal(oneChipRow * 2, row.ActualHeight, 0.5);
            Assert.Equal(oneChipComposer + oneChipRow, composer.ActualHeight, 0.5);
            Assert.True(BoundsIn(composer, row).Right <= composer.ActualWidth + 0.5);

            // What does not fit scrolls: the newest chip is brought into view, the wheel moves the chips, and the edge fades.
            var scroller = Descendants<ScrollViewer>(row).Single(viewer => viewer.Name == "PART_Scroller");
            WaitUntil(() => scroller.ScrollableHeight > 0 && scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5,
                "The chips did not scroll to the newest one.");
            Assert.NotNull(scroller.OpacityMask);
            var before = scroller.VerticalOffset;
            var wheel = new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, 120)
            {
                RoutedEvent = UIElement.PreviewMouseWheelEvent,
            };
            row.RaiseEvent(wheel);
            Assert.True(wheel.Handled);
            Pump();
            Assert.True(scroller.VerticalOffset < before);

            // Taking all but one off leaves one line again, with no fade.
            foreach (var chip in model.Chips.Skip(1).ToList())
            {
                model.RemoveAttachmentCommand.Execute(chip);
            }

            Pump();
            Assert.Single(model.Chips);
            Assert.Null(scroller.OpacityMask);
            Assert.Equal(oneChipRow, row.ActualHeight, 0.5);
            Assert.Equal(oneChipComposer, composer.ActualHeight, 0.5);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void EachKindOfChipShowsItsIcon_ItsNameCutToFit_AndAButtonThatTakesItOff() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var image = new ImageItem("sketch", DemoImages.Photos()[0].Thumbnail!);
        model.StartWithAttachment(image);
        model.Attach(new TextAttachment("Selected words"));
        model.Attach(Attached("Quarterly budget review with a very long name that cannot fit the composer at all.pdf"));
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var row = Named<AttachmentChipList>(panel, "ComposerAttachments");
            var chips = Descendants<Border>(row).Where(border => border.Name == "Chip").ToList();
            Assert.Equal(3, chips.Count);
            Assert.All(chips, chip => Assert.True(chip.ActualHeight is > 24 and < 32));

            // Picture: its thumbnail; text and file: a glyph.
            Assert.Equal(Visibility.Visible, Descendants<Border>(chips[0]).Single(border => border.Name == "Thumbnail").Visibility);
            Assert.Equal(Visibility.Collapsed, Descendants<System.Windows.Shapes.Path>(chips[0]).Single(path => path.Name == "Glyph").Visibility);
            foreach (var chip in chips.Skip(1))
            {
                Assert.Equal(Visibility.Collapsed, Descendants<Border>(chip).Single(border => border.Name == "Thumbnail").Visibility);
                Assert.Equal(Visibility.Visible, Descendants<System.Windows.Shapes.Path>(chip).Single(path => path.Name == "Glyph").Visibility);
            }

            var textGlyph = Descendants<System.Windows.Shapes.Path>(chips[1]).Single(path => path.Name == "Glyph").Data;
            var fileGlyph = Descendants<System.Windows.Shapes.Path>(chips[2]).Single(path => path.Name == "Glyph").Data;
            Assert.NotEqual(textGlyph.Bounds, fileGlyph.Bounds);

            // Names are cut with an ellipsis, never wider than the chip's place, and name the chip for assistive technology.
            var longName = Descendants<TextBlock>(chips[2]).Single();
            Assert.Equal(TextTrimming.CharacterEllipsis, longName.TextTrimming);
            Assert.True(chips[2].ActualWidth < 200, $"The file's chip is {chips[2].ActualWidth} wide.");
            Assert.Equal("Selected words", System.Windows.Automation.AutomationProperties.GetName(chips[1]));
            Assert.Equal(chips[1].ToolTip, model.Chips[1].Detail);

            // Each chip's button names what it removes.
            Assert.Equal(
                ["Remove attachment", "Remove attached text", "Remove attached file"],
                chips.Select(chip => System.Windows.Automation.AutomationProperties.GetName(Descendants<Button>(chip).Single())));
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void TheSentQuestionShowsItsTextAttachmentAboveTheBubble() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        model.Messages.Add(new MessageViewModel(
            MessageRole.User, "When is the launch?", null, null, [new TextAttachment("The launch moves to Friday.")]));
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var texts = Descendants<ItemsControl>(panel)
                .Single(control => control.Name == "TextAttachments");
            // Only its words show: the note about page text around the selection (step 88) is there but collapsed.
            Assert.Equal("The launch moves to Friday.", Descendants<TextBlock>(texts).Single(block => block.Visibility == Visibility.Visible).Text);
            Assert.Equal(Visibility.Collapsed, Descendants<TextBlock>(texts).Single(block => block.Name == "NearbyMarker").Visibility);
        }
        finally { panel.Close(); }
    }));

    [Fact]
    public void TheSentQuestionSaysWhenPageTextAroundTheSelectionWentWithIt() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var page = new WebPageOrigin("A page", "https://example.test/", "Microsoft Edge", "Before the words.", "After the words.");
        model.Messages.Add(new MessageViewModel(
            MessageRole.User, "When is the launch?", null, null, [new TextAttachment("The launch moves to Friday.", webPage: page)]));
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();

            var texts = Descendants<ItemsControl>(panel).Single(control => control.Name == "TextAttachments");
            var shown = Descendants<TextBlock>(texts).Where(block => block.Visibility == Visibility.Visible).Select(block => block.Text).ToArray();
            Assert.Equal(["The launch moves to Friday.", "+ nearby page text"], shown);
        }
        finally { panel.Close(); }
    }));

    // Opt-in render (ASSISTANT_UI_RENDER_DIR) of the row of chips, for looking at: a few of each kind, and so many that it scrolls.
    [Fact]
    public void TheChipsRenderAboveTheComposer() => RunSta(() => WithTheme(() =>
    {
        var (panel, model, _) = CreatePanel();
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Complete };
        answer.Content.Add(new TextContent("These appear to be a series of screenshots capturing various user interface elements."));
        model.Messages.Add(new MessageViewModel(MessageRole.User, "what are these images"));
        model.Messages.Add(answer);
        try
        {
            panel.ShowConversation();
            WaitUntil(() => Named<Grid>(panel, "SurfaceHost").Opacity == 1, "The panel did not finish showing.");
            Pump();
            var photos = DemoImages.Photos();
            model.Attach(new ImageItem("Screenshot 2026-06-30 at 4.31.47 PM.png", photos[0].Thumbnail!));
            model.Attach(Attached("Quarterly budget review.pdf"));
            model.Attach(new TextAttachment("Dear team, the launch moves to Friday."));
            Pump();
            RenderGlass(panel, "attachment-chips-few-2x.png", 2);

            model.Attach(new ImageItem("Screenshot 2026-06-30 at 4.30.05 PM.png", photos[1].Thumbnail!));
            model.Attach(new ImageItem("Screenshot 2026-06-30 at 4.28.18 PM.png", photos[2].Thumbnail!));
            model.Attach(new TextAttachment("A second selection that has a much longer first line than any chip could ever hold"));
            Pump();
            System.Threading.Thread.Sleep(100);
            Pump();
            RenderGlass(panel, "attachment-chips-many-2x.png", 2);
        }
        finally { panel.Close(); }
    }));

    // The same attachments, by reference.
    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public static ReferenceComparer Instance { get; } = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
