using System.Collections;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Views;

/// <summary>
/// The view that draws a conversation (PROJECT_SPEC §4.2, §4.3), shared by the floating conversation and the History
/// window's workspace so a message is drawn one way everywhere: the user's bubbles and the Assistant's typed content
/// parts, by the shared message templates, in a scroll viewer that fades toward its edges. It has no surface of its
/// own; the host supplies the glass, the messages, where they rest and how they fade.
/// </summary>
public partial class ConversationView : UserControl
{
    /// <summary>Identifies the <see cref="Messages"/> property.</summary>
    public static readonly DependencyProperty MessagesProperty = DependencyProperty.Register(
        nameof(Messages), typeof(IEnumerable), typeof(ConversationView),
        new PropertyMetadata(null, (d, e) => ((ConversationView)d).OnMessagesChanged(e)));

    /// <summary>Identifies the <see cref="ContentPadding"/> property.</summary>
    public static readonly DependencyProperty ContentPaddingProperty = DependencyProperty.Register(
        nameof(ContentPadding), typeof(Thickness), typeof(ConversationView), new PropertyMetadata(default(Thickness)));

    /// <summary>Identifies the <see cref="ColumnWidth"/> property.</summary>
    public static readonly DependencyProperty ColumnWidthProperty = DependencyProperty.Register(
        nameof(ColumnWidth), typeof(double), typeof(ConversationView), new PropertyMetadata(double.PositiveInfinity));

    /// <summary>Identifies the <see cref="TopFade"/> property.</summary>
    public static readonly DependencyProperty TopFadeProperty = DependencyProperty.Register(
        nameof(TopFade), typeof(PointCollection), typeof(ConversationView), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="BottomFade"/> property.</summary>
    public static readonly DependencyProperty BottomFadeProperty = DependencyProperty.Register(
        nameof(BottomFade), typeof(PointCollection), typeof(ConversationView), new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="CanAttach"/> property.</summary>
    public static readonly DependencyProperty CanAttachProperty = DependencyProperty.Register(
        nameof(CanAttach), typeof(bool), typeof(ConversationView), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="CanAttachDocuments"/> property.</summary>
    public static readonly DependencyProperty CanAttachDocumentsProperty = DependencyProperty.Register(
        nameof(CanAttachDocuments), typeof(bool), typeof(ConversationView), new PropertyMetadata(false));

    /// <summary>Identifies the <see cref="IsWorking"/> property.</summary>
    public static readonly DependencyProperty IsWorkingProperty = DependencyProperty.Register(
        nameof(IsWorking), typeof(bool), typeof(ConversationView), new PropertyMetadata(false, (d, e) => ((ConversationView)d).OnIsWorkingChanged((bool)e.NewValue)));

    /// <summary>Identifies the <see cref="WorkingText"/> property.</summary>
    public static readonly DependencyProperty WorkingTextProperty = DependencyProperty.Register(
        nameof(WorkingText), typeof(string), typeof(ConversationView), new PropertyMetadata(""));

    /// <summary>Identifies the <see cref="TailRoom"/> property.</summary>
    public static readonly DependencyProperty TailRoomProperty = DependencyProperty.Register(
        nameof(TailRoom), typeof(double), typeof(ConversationView), new PropertyMetadata(0.0));

    // How close to the end, in DIPs, still counts as showing the end.
    private const double EndTolerance = 1;

    // How much of what is above a message stays in view when it is scrolled to: enough to clear the fade under the buttons.
    private const double ScrollToMargin = 72;

    public ConversationView()
    {
        InitializeComponent();
        Transcript.ScrollChanged += OnScrollChanged;

        // A message that was sent comes to the top of the view, as the reference's does: what led to it is above it, a scroll away.
        AddHandler(Appear.ArrivedEvent, new RoutedEventHandler(OnPartArrived));

        // The copy button of a block of code in any message.
        CommandBindings.Add(new CommandBinding(
            CodeBlock.CopyCommand, OnCopyCode, (_, e) => e.CanExecute = e.Parameter is string));

        // The copy button under an answer.
        CommandBindings.Add(new CommandBinding(
            MessageActions.Copy,
            (_, e) =>
            {
                if (e.Parameter is MessageViewModel { CanCopy: true } answer)
                {
                    CopyText(answer.CopyText);
                    e.Handled = true;
                }
            },
            (_, e) =>
            {
                // The button is only there once the answer can be copied; asking again each time it could have changed would only leave it greyed out.
                e.CanExecute = e.Parameter is MessageViewModel;
                e.Handled = true;
            }));

        // What can be done with a file in an answer: open it, show it in File Explorer, attach it to the conversation.
        CommandBindings.Add(new CommandBinding(
            FileActions.Open, (_, e) => Launch(e, (launcher, path) => launcher.Open(path)), CanLaunch));
        CommandBindings.Add(new CommandBinding(
            FileActions.Reveal, (_, e) => Launch(e, (launcher, path) => launcher.Reveal(path)), CanLaunch));
        CommandBindings.Add(new CommandBinding(FileActions.Attach, OnAttach, CanAttachItem));
    }

    /// <summary>Raised when the user asks to attach an image of an answer to the conversation.</summary>
    public event EventHandler<ImageItem>? AttachRequested;

    /// <summary>Raised when the user asks to attach a document of an answer to the conversation.</summary>
    public event EventHandler<DocumentAttachment>? DocumentAttachRequested;

    /// <summary>
    /// Opens files and shows them in File Explorer for the open and show commands; without one, those commands cannot run.
    /// </summary>
    public IFileLauncher? FileLauncher { get; set; }

    /// <summary>
    /// Whether the host can attach an image to its conversation. Where it cannot, the attach command cannot run for an image and
    /// its menu item is dimmed.
    /// </summary>
    public bool CanAttach
    {
        get => (bool)GetValue(CanAttachProperty);
        set => SetValue(CanAttachProperty, value);
    }

    /// <summary>
    /// Whether the host can attach a document to its conversation, which the History window's composer can while it cannot take
    /// an image yet. Where it cannot, the attach command cannot run for a document and its menu item is dimmed.
    /// </summary>
    public bool CanAttachDocuments
    {
        get => (bool)GetValue(CanAttachDocumentsProperty);
        set => SetValue(CanAttachDocumentsProperty, value);
    }

    /// <summary>The messages to draw, oldest first, each a <c>MessageViewModel</c>.</summary>
    public IEnumerable? Messages
    {
        get => (IEnumerable?)GetValue(MessagesProperty);
        set => SetValue(MessagesProperty, value);
    }

    /// <summary>
    /// Where the messages rest, inside the scrolled area: the space around them, whose sides are the column's margins
    /// (and so the room a card reaches into), and whose top and bottom keep the first and last messages clear of the
    /// host's controls at rest.
    /// </summary>
    public Thickness ContentPadding
    {
        get => (Thickness)GetValue(ContentPaddingProperty);
        set => SetValue(ContentPaddingProperty, value);
    }

    /// <summary>How wide the column of messages, padding included, may be; wider views center it.</summary>
    public double ColumnWidth
    {
        get => (double)GetValue(ColumnWidthProperty);
        set => SetValue(ColumnWidthProperty, value);
    }

    /// <summary>How the messages fade toward the top edge; see <see cref="FadingScrollViewer.TopFade"/>.</summary>
    public PointCollection? TopFade
    {
        get => (PointCollection?)GetValue(TopFadeProperty);
        set => SetValue(TopFadeProperty, value);
    }

    /// <summary>How the messages fade toward the bottom edge; see <see cref="FadingScrollViewer.BottomFade"/>.</summary>
    public PointCollection? BottomFade
    {
        get => (PointCollection?)GetValue(BottomFadeProperty);
        set => SetValue(BottomFadeProperty, value);
    }

    /// <summary>
    /// Whether the Assistant is working on the answer to the last message: the Searching indicator turns under it, where the answer will be, until
    /// its words come. The host says so; the view only draws it.
    /// </summary>
    public bool IsWorking
    {
        get => (bool)GetValue(IsWorkingProperty);
        set => SetValue(IsWorkingProperty, value);
    }

    /// <summary>What the Assistant is doing while <see cref="IsWorking"/>, in a word or two ("Searching"): what a screen reader says of the indicator.</summary>
    public string WorkingText
    {
        get => (string)GetValue(WorkingTextProperty);
        set => SetValue(WorkingTextProperty, value);
    }

    /// <summary>
    /// Empty room under the last message, in DIPs: as much as it takes for an answer that was brought to the top of the view to stay there. It goes when
    /// the next message is sent.
    /// </summary>
    public double TailRoom
    {
        get => (double)GetValue(TailRoomProperty);
        private set => SetValue(TailRoomProperty, value);
    }

    /// <summary>Puts text on the clipboard for the copy button under an answer. A test gives its own, so that it never writes to the clipboard of the PC it runs on.</summary>
    internal Action<string> CopyText { get; set; } = SetClipboard;

    /// <summary>The scroll viewer the messages are in.</summary>
    internal FadingScrollViewer Viewer => Transcript;

    /// <summary>The control that holds the messages.</summary>
    internal ItemsControl List => MessageList;

    /// <summary>Scrolls to the newest message.</summary>
    public void ScrollToEnd() => Transcript.ScrollToEnd();

    /// <summary>
    /// Scrolls so <paramref name="message"/> is in view, with a little of the message before it above it, such as the
    /// message a search result matched. Nothing moves when the message is not one the view has drawn yet.
    /// </summary>
    /// <returns><see langword="true"/> when the message was found.</returns>
    public bool ScrollTo(MessageViewModel message)
    {
        ArgumentNullException.ThrowIfNull(message);
        MessageList.UpdateLayout();
        if (MessageList.ItemContainerGenerator.ContainerFromItem(message) is not FrameworkElement container)
        {
            return false;
        }

        var top = container.TranslatePoint(default, MessageList).Y;
        Transcript.ScrollToVerticalOffset(Math.Clamp(top - ScrollToMargin, 0, Transcript.ScrollableHeight));
        return true;
    }

    /// <summary>Moves keyboard focus to the conversation, so Page Up and Page Down scroll it.</summary>
    public new bool Focus() => Transcript.Focus();

    /// <summary>Whether an element is one of the conversation's messages.</summary>
    internal bool IsMessage(DependencyObject element) =>
        element is FrameworkElement item && ItemsControl.ItemsControlFromItemContainer(item) == MessageList;

    // A message that grows, as an answer does while it streams in, is followed while the end was in view: the newest
    // words stay on screen. Someone who has scrolled back to read is left where they are.
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // The event bubbles up from scroll viewers inside messages too, such as a code block's.
        if (!ReferenceEquals(e.OriginalSource, Transcript) || e.ExtentHeightChange <= 0)
        {
            return;
        }

        var endBefore = e.ExtentHeight - e.ExtentHeightChange;
        var shownBefore = e.VerticalOffset - e.VerticalChange + e.ViewportHeight - e.ViewportHeightChange;
        if (shownBefore >= endBefore - EndTolerance)
        {
            Transcript.ScrollToEnd();
        }
    }

    // A message the user just sent brings the end into view, even when they had scrolled back to read: they are
    // asking for what comes next.
    private void OnMessagesChanged(DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyCollectionChanged old)
        {
            old.CollectionChanged -= OnMessagesCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged current)
        {
            current.CollectionChanged += OnMessagesCollectionChanged;
        }
    }

    private void OnMessagesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove)
        {
            TailRoom = 0;
        }

        if (e.Action == NotifyCollectionChangedAction.Add
            && e.NewItems?.OfType<MessageViewModel>().Any(message => message.Role == MessageRole.User) == true)
        {
            // The next question starts under what is there: the room an answer was given to stand at the top is given back.
            TailRoom = 0;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Transcript.ScrollToEnd);
        }
    }

    // A message that was sent has come into the answer: the answer's words about it ("It's sent.") go to where the first message rests, with the
    // message under them, and the question, the steps and the request that led to it are above, a scroll away.
    private void OnPartArrived(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: SentMessageContent } part)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => BringToTop(part));
        }
    }

    private void BringToTop(FrameworkElement part, bool again = true)
    {
        if (!part.IsLoaded || !IsAncestorOf(part))
        {
            return;
        }

        // The words that come before the message in the same answer, when there are some; else the message itself.
        FrameworkElement lead = part;
        for (DependencyObject? element = part; element is not null && !ReferenceEquals(element, MessageList); element = VisualTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement { DataContext: MessageViewModel } answer)
            {
                lead = WordsBefore(answer, part) ?? part;
                break;
            }
        }

        MessageList.UpdateLayout();
        var top = lead.TranslatePoint(default, MessageList).Y - ContentPadding.Top;
        if (top <= 0)
        {
            return;
        }

        // Enough room under the last message for that place to be reachable. How tall the messages are is what they ask for, not what they are given:
        // a conversation shorter than the view is stretched to fill it.
        var extent = Math.Min(MessageList.ActualHeight, MessageList.DesiredSize.Height) - TailRoom;
        TailRoom = Math.Max(0, top + Transcript.ViewportHeight - extent);
        MessageList.UpdateLayout();
        Transcript.ScrollToVerticalOffset(top);

        // What is above may still be settling into its size (text that is laid out a moment after it is shown): once everything rests, the place is
        // worked out once more from where things then are.
        if (again)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => BringToTop(part, again: false));
        }
    }

    // The last run of words drawn above the part in the answer it belongs to.
    private static FrameworkElement? WordsBefore(DependencyObject answer, FrameworkElement part)
    {
        FrameworkElement? found = null;
        var partTop = part.TranslatePoint(default, (UIElement)answer).Y;
        void Look(DependencyObject parent)
        {
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index);
                if (child is ContentPresenter { Content: TextContent } words && words.TranslatePoint(default, (UIElement)answer).Y < partTop)
                {
                    found = words;
                }
                else
                {
                    Look(child);
                }
            }
        }

        Look(answer);
        return found;
    }

    // The indicator arriving is the newest thing in the conversation: it is brought into view, as a message just sent is.
    private void OnIsWorkingChanged(bool working)
    {
        if (working)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, Transcript.ScrollToEnd);
        }
    }

    private void CanLaunch(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = FileLauncher is not null && FileActions.PathOf(e.Parameter) is not null;
        e.Handled = true;
    }

    private void Launch(ExecutedRoutedEventArgs e, Func<IFileLauncher, string, bool> action)
    {
        if (FileLauncher is { } launcher && FileActions.PathOf(e.Parameter) is { } path)
        {
            action(launcher, path);
            e.Handled = true;
        }
    }

    private void CanAttachItem(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = (CanAttach && FileActions.ImageOf(e.Parameter) is not null)
            || (CanAttachDocuments && FileActions.DocumentOf(e.Parameter) is not null);
        e.Handled = true;
    }

    private void OnAttach(object sender, ExecutedRoutedEventArgs e)
    {
        if (CanAttach && FileActions.ImageOf(e.Parameter) is { } image)
        {
            AttachRequested?.Invoke(this, image);
            e.Handled = true;
        }
        else if (CanAttachDocuments && FileActions.DocumentOf(e.Parameter) is { } document)
        {
            DocumentAttachRequested?.Invoke(this, document);
            e.Handled = true;
        }
    }

    private static void OnCopyCode(object sender, ExecutedRoutedEventArgs e) => SetClipboard((string)e.Parameter);

    // Off the window's own thread: a clipboard that another program is looking at (a clipboard manager, Windows' own history) is waited for there, and the
    // window does not stand still for it. When it stays held nothing is copied, and the next click may work.
    private static void SetClipboard(string text) => _ = Assistant.Windows.Clipboard.TextClipboardWriter.SetTextInBackground(text);
}
