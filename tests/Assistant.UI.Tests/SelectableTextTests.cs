using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Assistant.Core.Domain;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Xunit;

namespace Assistant.UI.Tests;

// The words of a message can be selected and copied in the full window, block by block; in the bar's conversation they are plain text as before.
public sealed partial class PromptInputControlTests
{
    private static (Window Window, ConversationView View) ShowSelectable(bool selectable, params MessageViewModel[] messages)
    {
        var view = new ConversationView
        {
            Messages = new ObservableCollection<MessageViewModel>(messages), ContentPadding = new Thickness(30, 40, 30, 40), Width = 418, Height = 640,
        };
        view.CopyText = _ => { };
        if (selectable)
        {
            SelectableTextBlock.SetIsSelectionEnabled(view, true);
        }

        var window = new Window
        {
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E)), Child = view },
            Width = 460, Height = 700, Left = -10000, Top = -10000, ShowActivated = false, Opacity = 0,
        };
        window.Show();
        Pump();
        return (window, view);
    }

    private static MessageViewModel[] AQuestionAndItsAnswer()
    {
        var question = new MessageViewModel(MessageRole.User, "what is the capital of France?");
        var answer = new MessageViewModel(MessageRole.Assistant) { Status = MessageStatus.Complete };
        answer.Content.Add(new TextContent("# France\n\nThe capital of France is **Paris**.\n\n- It is on the Seine.\n- It has about two million people."));
        return [question, answer];
    }

    [Fact]
    public void InTheFullViewTheWordsOfAMessageCanBeSelected_BlockByBlock() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        using var errors = OfferBindingErrors.Listen();
        var (window, view) = ShowSelectable(true, AQuestionAndItsAnswer());
        try
        {
            var blocks = Descendants<SelectableTextBlock>(view).Where(block => block.IsVisible).ToList();

            // What the user sent, the heading, the paragraph and the two list items: every one can be selected in.
            Assert.Equal(5, blocks.Count);
            Assert.All(blocks, block => Assert.True(block.IsSelectable, block.Text));
            Assert.All(blocks, block => Assert.True(block.Focusable));
            Assert.All(blocks, block => Assert.False(KeyboardNavigation.GetIsTabStop(block)));

            var sent = blocks.Single(block => block.Text == "what is the capital of France?");
            Assert.Equal("", sent.SelectedText);
            sent.SelectAll();
            Assert.Equal("what is the capital of France?", sent.SelectedText);

            // An answer's words are selected as they read, without the marks that made them bold.
            var paragraph = blocks.Single(block => block.Text == "The capital of France is Paris.");
            paragraph.SelectAll();
            Assert.Equal("The capital of France is Paris.", paragraph.SelectedText);
            Assert.Equal("It is on the Seine.", Selected(blocks.Single(block => block.Text == "It is on the Seine.")));

            // Under the right mouse button: Copy and Select all, in the Assistant's own menu.
            var menu = Assert.IsType<ContextMenu>(paragraph.TryFindResource(SelectableTextBlock.MenuKey));
            Assert.Equal(["Copy", "Select all"], menu.Items.OfType<MenuItem>().Select(item => (string)item.Header));
            Assert.Same(ApplicationCommands.Copy, menu.Items.OfType<MenuItem>().First().Command);

            // A selection is one block's: taking the keyboard in another lets go of it. (The window of a test is never the active one, so the keyboard is
            // said to have arrived.)
            sent.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, sent) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            paragraph.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, sent, paragraph) { RoutedEvent = Keyboard.GotKeyboardFocusEvent });
            Assert.Equal("", sent.SelectedText);
            Assert.Equal("The capital of France is Paris.", paragraph.SelectedText);

            // What is selected is drawn marked; the editor marks it when the block has the keyboard, which a test's window never has, so it is asked to here.
            paragraph.Selection!.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .First(method => method.Name.EndsWith("UpdateCaretAndHighlight", StringComparison.Ordinal) && method.GetParameters().Length == 0)
                .Invoke(paragraph.Selection, null);
            Pump();
            RenderFixture(view, "history-text-selected.png", 2);

            // (The mask of an attached picture's template says once, as the template is read, that it has no element yet: that is not this.)
            var said = string.Concat(errors.Messages);
            Assert.DoesNotContain("SelectableTextBlock", said, StringComparison.Ordinal);
            Assert.DoesNotContain("MenuItem", said, StringComparison.Ordinal);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }

        static string Selected(SelectableTextBlock block)
        {
            block.SelectAll();
            return block.SelectedText;
        }
    });

    [Fact]
    public void InTheBarsConversationTheWordsAreTextAsBefore() => RunSta(() =>
    {
        Appear.Enabled = false;
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative) });
        var (window, view) = ShowSelectable(false, AQuestionAndItsAnswer());
        try
        {
            var blocks = Descendants<SelectableTextBlock>(view).Where(block => block.IsVisible).ToList();
            Assert.Equal(5, blocks.Count);
            Assert.All(blocks, block => Assert.False(block.IsSelectable));
            Assert.All(blocks, block => Assert.False(block.Focusable));
            blocks[0].SelectAll();
            Assert.Equal("", blocks[0].SelectedText);
        }
        finally
        {
            Appear.Enabled = true;
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });
}
