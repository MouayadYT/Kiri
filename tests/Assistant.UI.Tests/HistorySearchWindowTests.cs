using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Assistant.Core.Domain;
using Assistant.UI.Controls;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // Three saved conversations, listed without their messages, and a search that finds by whole words in these titles
    // and answers.
    private static (FakeHistorySource Source, HistoryConversation Weather, HistoryConversation Pasta, HistoryConversation Photos)
        SavedConversations()
    {
        var source = new FakeHistorySource();
        var weather = new HistoryConversation(Guid.NewGuid(), null, ReferenceNow.AddMinutes(-10), "Weekend weather")
        {
            LatestAnswerText = "Saturday looks sunny with a high of 22 degrees.",
        };
        var pasta = new HistoryConversation(Guid.NewGuid(), null, ReferenceNow.AddMinutes(-100), "Pasta dinner")
        {
            LatestAnswerText = "Cook the pasta with garlic and spinach.",
        };
        var photos = new HistoryConversation(Guid.NewGuid(), null, ReferenceNow.AddDays(-2), "Photos")
        {
            LatestAnswerText = "I found four photos.",
        };
        source.Listed.AddRange([weather, pasta, photos]);
        source.Messages[weather.Id] = [new MessageViewModel(MessageRole.User, "What is the weather this weekend?"), new MessageViewModel(MessageRole.Assistant, "Saturday looks sunny with a high of 22 degrees.")];
        source.Messages[pasta.Id] = [new MessageViewModel(MessageRole.User, "What can I cook tonight?"), new MessageViewModel(MessageRole.Assistant, "Cook the pasta with garlic and spinach.")];
        source.Messages[photos.Id] = [new MessageViewModel(MessageRole.User, "Show me photos"), new MessageViewModel(MessageRole.Assistant, "I found four photos.")];
        source.Found = query =>
        {
            var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return
            [
                .. new[] { weather, pasta, photos }
                    .Where(conversation => words.All(word =>
                        (conversation.Title! + " " + conversation.LatestAnswerText).Contains(word, StringComparison.OrdinalIgnoreCase)))
                    .Select(conversation =>
                    {
                        var text = conversation.LatestAnswerText!;
                        var at = text.IndexOf(words[0], StringComparison.OrdinalIgnoreCase);
                        return at < 0
                            ? new HistorySearchHit(conversation)
                            : new HistorySearchHit(conversation, text, [new TextMatch(at, words[0].Length)]);
                    }),
            ];
        };
        return (source, weather, pasta, photos);
    }

    private static void TypeInSearch(HistoryWindow window, string text)
    {
        var input = Named<PromptInputControl>(window, "SearchInput");

        // The field's template is applied once it is laid out, which it is when it shows.
        window.UpdateLayout();
        Editor(input).Text = text;
        Pump();
    }

    [Fact]
    public void TheSearchButtonOpensTheFieldInItsPlace_AndTheKeyboardGoesToIt() => RunSta(() => WithTheme(() =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            var button = Named<Button>(window, "SearchButton");
            var field = Named<Grid>(window, "SearchField");
            Assert.True(button.IsEnabled);
            Assert.Equal(Visibility.Visible, button.Visibility);
            Assert.Equal(Visibility.Collapsed, field.Visibility);

            Click(button);
            Pump();

            Assert.True(history.IsSearchOpen);
            Assert.Equal(Visibility.Collapsed, button.Visibility);
            Assert.Equal(Visibility.Visible, field.Visibility);
            var input = Named<PromptInputControl>(window, "SearchInput");
            Assert.Equal("Search", input.Placeholder);
            Assert.Same(Editor(input), FocusManager.GetFocusedElement(window));

            // The field takes the button's place at the workspace's top right, as tall as the button was.
            var root = Named<Grid>(window, "Root");
            Assert.Equal(36, BoundsIn(root, field).Height, 1);
            Assert.Equal(264, BoundsIn(root, field).Width, 1);
            Assert.Equal(1440 - 7, BoundsIn(root, field).Right, 1.5);
            Assert.Equal(6.25, BoundsIn(root, field).Top, 1);
            RenderFixture(root, "history-search-field-2x.png", 2);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void TypingInTheFieldListsOnlyTheMatchingConversations_AsCardsAndAsRows() => RunSta(() => WithTheme(() =>
    {
        var (source, _, pasta, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            history.OpenSearchCommand.Execute(null);
            var cards = Named<ListBox>(window, "Conversations");
            var rows = Named<ListBox>(window, "ConversationRows");
            Assert.Equal(3, cards.Items.Count);

            TypeInSearch(window, "pasta");

            Assert.Equal(["Pasta dinner"], cards.Items.Cast<HistoryConversationViewModel>().Select(conversation => conversation.Title));
            Assert.Equal(pasta.Id, ((HistoryConversationViewModel)cards.Items[0]).Id);
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "NoResults").Visibility);

            // The list shows the same results, under the headers they belong to.
            history.Layout = HistoryLayout.List;
            Pump();
            Assert.Equal(["Pasta dinner"], rows.Items.Cast<HistoryConversationViewModel>().Select(conversation => conversation.Title));
            Assert.Equal(["Today"], Descendants<TextBlock>(rows).Where(text => text.Style == (Style)Application.Current.FindResource("Text.HistorySection")).Select(text => text.Text));
            RenderFixture(Named<Grid>(window, "Root"), "history-search-list-2x.png", 2);

            // Clearing the field lists everything again.
            TypeInSearch(window, "");
            Assert.Equal(3, cards.Items.Count);
            Assert.Equal(3, rows.Items.Count);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void ASearchThatFindsNothingSaysSo_UntilSomethingMatchesAgain() => RunSta(() => WithTheme(() =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            history.OpenSearchCommand.Execute(null);
            var noResults = Named<TextBlock>(window, "NoResults");
            Assert.Equal("No Results", noResults.Text);
            Assert.Equal(Visibility.Collapsed, noResults.Visibility);

            TypeInSearch(window, "zebra");

            Assert.Equal(Visibility.Visible, noResults.Visibility);
            Assert.Empty(Named<ListBox>(window, "Conversations").Items);
            RenderFixture(Named<Grid>(window, "Root"), "history-search-none-2x.png", 2);

            TypeInSearch(window, "photos");

            Assert.Equal(Visibility.Collapsed, noResults.Visibility);
            Assert.Single(Named<ListBox>(window, "Conversations").Items);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void AResultsCardShowsTheMatchedWordsPickedOut_InsteadOfItsPreview() => RunSta(() => WithTheme(() =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            history.OpenSearchCommand.Execute(null);

            TypeInSearch(window, "garlic");
            Pump();

            var cards = Named<ListBox>(window, "Conversations");
            var card = (ListBoxItem)cards.ItemContainerGenerator.ContainerFromIndex(0);
            var preview = Descendants<TextBlock>(card).Single(text => text.Name == "Preview");
            Assert.Equal(Visibility.Visible, preview.Visibility);
            Assert.Equal("Cook the pasta with garlic and spinach.", preview.Text);
            RenderFixture(Named<Grid>(window, "Root"), "history-search-card-2x.png", 2);
            var picked = preview.Inlines.OfType<System.Windows.Documents.Run>().Where(run => run.FontWeight == FontWeights.SemiBold).ToArray();
            Assert.Equal(["garlic"], picked.Select(run => run.Text));
            Assert.Equal("Cook the pasta with garlic and spinach.", string.Concat(preview.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text)));

            // Cleared, it is the plain preview again.
            TypeInSearch(window, "");
            Pump();
            var again = Descendants<TextBlock>((ListBoxItem)cards.ItemContainerGenerator.ContainerFromItem(history.Conversations.Single(conversation => conversation.Title == "Pasta dinner")))
                .Single(text => text.Name == "Preview");
            Assert.DoesNotContain(again.Inlines.OfType<System.Windows.Documents.Run>(), run => run.FontWeight == FontWeights.SemiBold);
            Assert.Equal("Cook the pasta with garlic and spinach.", again.Text);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void EscPutsTheSearchAway_AndListsEveryConversationAgain() => RunSta(() => WithTheme(() =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            history.OpenSearchCommand.Execute(null);
            TypeInSearch(window, "pasta");
            Assert.Single(Named<ListBox>(window, "Conversations").Items);

            PressPreviewKey(window, Key.Escape);
            Pump();

            Assert.False(history.IsSearchOpen);
            Assert.Equal(string.Empty, history.SearchText);
            Assert.Equal(3, Named<ListBox>(window, "Conversations").Items.Count);
            Assert.Equal(Visibility.Visible, Named<Button>(window, "SearchButton").Visibility);
            Assert.Equal(Visibility.Collapsed, Named<Grid>(window, "SearchField").Visibility);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void TheButtonAtTheEndOfTheFieldClearsTheSearchToo() => RunSta(() => WithTheme(() =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            history.OpenSearchCommand.Execute(null);
            TypeInSearch(window, "pasta");

            var close = Named<Button>(window, "CloseSearchButton");
            Assert.Equal("Close search", System.Windows.Automation.AutomationProperties.GetName(close));
            Click(close);
            Pump();

            Assert.False(history.IsSearchOpen);
            Assert.Equal(3, Named<ListBox>(window, "Conversations").Items.Count);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void FindOpensTheSearchFromTheKeyboard_AndCtrlFIsItsShortcut() => RunSta(() => WithTheme(() =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();

            Assert.True(ApplicationCommands.Find.CanExecute(null, window));
            ApplicationCommands.Find.Execute(null, window);
            Pump();

            Assert.True(history.IsSearchOpen);
            Assert.Contains(ApplicationCommands.Find.InputGestures.OfType<KeyGesture>(), gesture => gesture.Key == Key.F && gesture.Modifiers == ModifierKeys.Control);
            Assert.Same(Editor(Named<PromptInputControl>(window, "SearchInput")), FocusManager.GetFocusedElement(window));
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void TheOpenConversationStaysInTheWorkspace_WhileASearchLeavesItOutOfTheSidebar() => RunSta(() => WithTheme(() =>
    {
        var (source, weather, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            var cards = Named<ListBox>(window, "Conversations");
            var open = history.Conversations.Single(conversation => conversation.Id == weather.Id);
            cards.SelectedItem = open;
            Pump();
            Assert.Same(open, history.Selected);
            history.OpenSearchCommand.Execute(null);

            TypeInSearch(window, "pasta");
            Pump();

            Assert.Same(open, history.Selected);
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "EmptyState").Visibility);
            Assert.Equal(Visibility.Visible, Named<ConversationView>(window, "Conversation").Visibility);

            // Back in the list once the search is over, and lit as the open one again.
            TypeInSearch(window, "");
            Pump();
            Pump();
            Assert.Same(open, history.Selected);
            Assert.Same(open, cards.SelectedItem);
            Assert.Equal(0, cards.SelectedIndex);
            Assert.True(((ListBoxItem)cards.ItemContainerGenerator.ContainerFromItem(open)).IsSelected);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void InTheListTooTheOpenConversationStaysOpen_AndItsRowIsLitAgainWhenTheSearchIsOver() => RunSta(() => WithTheme(() =>
    {
        var (source, weather, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        history.Layout = HistoryLayout.List;
        try
        {
            window.Show();
            Pump();
            var rows = Named<ListBox>(window, "ConversationRows");
            var open = history.Conversations.Single(conversation => conversation.Id == weather.Id);
            history.Selected = open;
            Pump();
            Assert.Same(open, rows.SelectedItem);
            history.OpenSearchCommand.Execute(null);

            TypeInSearch(window, "pasta");
            Pump();

            Assert.Same(open, history.Selected);
            Assert.Single(rows.Items);
            Assert.Equal(Visibility.Collapsed, Named<TextBlock>(window, "EmptyState").Visibility);

            TypeInSearch(window, "");
            Pump();
            Pump();

            Assert.Same(open, history.Selected);
            Assert.Equal(3, rows.Items.Count);
            Assert.Same(open, rows.SelectedItem);
            Assert.True(RowOf(rows, open).IsSelected);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void ChoosingAResultOpensItsConversation_WhoseMessagesAreReadWhenItIsChosen() => RunSta(() => WithTheme(() =>
    {
        var (source, _, pasta, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();
            history.OpenSearchCommand.Execute(null);
            TypeInSearch(window, "pasta");
            var cards = Named<ListBox>(window, "Conversations");

            cards.SelectedIndex = 0;
            Pump();
            Pump();

            Assert.Equal(pasta.Id, history.Selected!.Id);
            Assert.Equal([pasta.Id], source.Loads);
            var transcript = Named<FadingScrollViewer>(window, "Transcript");
            Assert.Contains(Descendants<TextBlock>(transcript), text => text.Text == "What can I cook tonight?");
            Assert.Contains(Descendants<TextBlock>(transcript), text => text.Text == "Cook the pasta with garlic and spinach.");
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void ShowingTheWindowReadsTheSavedHistoryAgain() => RunSta(() => WithTheme(() =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, history, _) = CreateHistoryWindow(source: source);
        try
        {
            var calls = source.ListCalls;
            source.Listed.Add(new HistoryConversation(Guid.NewGuid(), null, ReferenceNow.AddMinutes(-1), "Saved a moment ago"));

            window.ShowAndActivate();
            Pump();

            Assert.Equal(calls + 1, source.ListCalls);
            Assert.Equal("Saved a moment ago", history.Conversations[0].Title);
            Assert.Equal(4, Named<ListBox>(window, "Conversations").Items.Count);
        }
        finally { window.CloseForGood(); }
    }));

    [Fact]
    public void ASavedConversationsCardShowsItsTitleTimeAndPreviewBeforeItsMessagesAreRead() => RunSta(() => WithTheme(() => WithCulture("en-US", () =>
    {
        var (source, _, _, _) = SavedConversations();
        var (window, _, _) = CreateHistoryWindow(source: source);
        try
        {
            window.Show();
            Pump();

            var card = (ListBoxItem)Named<ListBox>(window, "Conversations").ItemContainerGenerator.ContainerFromIndex(0);
            var texts = Descendants<TextBlock>(card).Select(text => text.Text).ToArray();
            Assert.Contains("Saturday looks sunny with a high of 22 degrees.", texts);
            Assert.Contains("Weekend weather", string.Concat(Descendants<FittedTitle>(card).Select(title => title.Text)));
            Assert.Empty(source.Loads);
        }
        finally { window.CloseForGood(); }
    })));

    [Fact]
    public void SearchHighlightPicksOutTheMatchesAndKeepsTheTextWhole() => RunSta(() =>
    {
        var block = new TextBlock();
        SearchHighlight.SetText(block, "Cook the pasta with garlic");
        SearchHighlight.SetMatches(block, [new TextMatch(9, 5), new TextMatch(20, 6)]);

        var runs = block.Inlines.OfType<System.Windows.Documents.Run>().ToArray();
        Assert.Equal(["Cook the ", "pasta", " with ", "garlic"], runs.Select(run => run.Text));
        Assert.Equal([FontWeights.Normal, FontWeights.SemiBold, FontWeights.Normal, FontWeights.SemiBold], runs.Select(run => run.FontWeight));
        Assert.Equal("Cook the pasta with garlic", block.Text);

        // Matches outside the text, or overlapping, are ignored; none makes plain text again.
        SearchHighlight.SetMatches(block, [new TextMatch(9, 5), new TextMatch(10, 3), new TextMatch(500, 4)]);
        Assert.Equal(["Cook the ", "pasta", " with garlic"], block.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
        SearchHighlight.SetMatches(block, null);
        Assert.Equal(["Cook the pasta with garlic"], block.Inlines.OfType<System.Windows.Documents.Run>().Select(run => run.Text));
        SearchHighlight.SetText(block, string.Empty);
        Assert.Empty(block.Inlines);
        Assert.Equal(string.Empty, block.Text);
    });
}
