using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Assistant.Core.Contracts;
using Assistant.UI.Controls;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Backdrop;
using Assistant.Windows.Frame;
using Assistant.Windows.Placement;

namespace Assistant.UI.Views;

/// <summary>
/// The Assistant's full window (PROJECT_SPEC §4.3): a resizable top-level window, in the taskbar, with the conversations
/// in a sidebar on the left, as cards or as a list as its view menu chooses, and the open one in the workspace on the
/// right. Windows draws its frame and a dark backdrop; the window draws everything inside it, its own close, minimize
/// and maximize buttons included. Closing it only hides it, so it opens again as it was.
/// </summary>
public partial class HistoryWindow : Window, IHistoryWindow
{
    /// <summary>Opens the view menu under the sidebar's filter button.</summary>
    public static readonly RoutedCommand ShowViewMenuCommand = new(nameof(ShowViewMenuCommand), typeof(HistoryWindow));

    // The window opens at the size of its reference, or smaller on a smaller screen, leaving this much of the work area
    // around it.
    private const double ScreenMargin = 24;

    // The hairline Windows draws around the window: a neutral gray a little lighter than the workspace, like the
    // reference's rim. It is opaque, so it has no hue of its own for the wallpaper to tint.
    private const int BorderColor = 0x3C3C3C;

    private readonly HistoryViewModel _history;
    private readonly IWindowPlacementService _placement;
    private bool _placed;
    private bool _closing;

    public HistoryWindow(
        HistoryViewModel history, IWindowFrameFactory frames, IWindowPlacementService placement,
        IWindowBackdropFactory backdrops, IFileLauncher? fileLauncher = null)
    {
        InitializeComponent();
        _history = history;
        _placement = placement;
        DataContext = history;

        // A file in a conversation opens, shows in File Explorer, or (a document the Assistant reads) is attached to the open
        // conversation for the next message. Attaching an image waits for the composer to take pictures.
        Conversation.FileLauncher = fileLauncher;
        Conversation.CanAttachDocuments = true;
        Conversation.DocumentAttachRequested += (_, document) => history.Attach(document);
        WindowFrameHost.Attach(this, frames, new WindowFrameStyle(SystemBackdropKind.Mica, BorderColor));

        // The composer's plus offers Photos and Files, as the floating conversation's does; what is chosen is attached for the next message.
        PlusMenu = AttachMenu.Attach(
            AttachButton, backdrops,
            pictures => AttachAndCompose(() => { foreach (var picture in pictures) history.Attach(picture); }),
            documents => AttachAndCompose(() => { foreach (var document in documents) history.Attach(document); }));

        // Handy hands its transcript over by pasting it: while a spoken request waits for it, the paste is taken here, wherever the keyboard is.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.V && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control
                && Assistant.UI.Voice.HandySpeechToTextService.TryAcceptClipboard())
            {
                e.Handled = true;
            }
        };

        // A pointer click on the microphone leaves the keyboard in the composer, as a click on the bar's microphone does: Handy hands its words over by
        // pasting them into whatever has the keyboard, and the composer takes that paste as what was said (it listens while the microphone is on), as
        // the window does above. With the keyboard left on the button, the words had nowhere of the composer's to arrive.
        ComposerVoiceButton.Click += (_, _) =>
        {
            if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is System.Windows.Input.MouseDevice or System.Windows.Input.StylusDevice)
            {
                Activate();
                ComposerInput.FocusInput();
            }
        };

        // Pictures on the clipboard are pasted into the composer like text: they are attached for the next message.
        ComposerInput.PicturesPasted += (_, pictures) => AttachAndCompose(() => { foreach (var picture in pictures) history.Attach(picture); });

        // The view menu is glass like the Assistant's own: it blurs what lies behind it while it is open.
        var menuCorners = (double)FindResource("Radius.Menu");
        MenuBackdrop.Attach(ViewMenu, "Glass", size => PanelShape.GetBackdropCornerRadii(size, menuCorners), backdrops);
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible && ViewMenu.IsOpen)
            {
                ViewMenu.IsOpen = false;
            }
        };

        StateChanged += (_, _) => KeepContentOnScreen();
        DpiChanged += (_, _) => KeepContentOnScreen();

        // A conversation opens at its newest message, as in the floating conversation.
        history.PropertyChanged += OnHistoryChanged;

        // A search leaves out the conversations it did not find, in both views of the list.
        history.ListedConversationsChanged += (_, _) => ListAgain();

        // A new conversation is for typing in: the keyboard goes to the composer once it shows.
        history.NewConversationStarted += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_history.HasSelection)
            {
                ComposerInput.FocusInput();
            }
        });

        // The microphone is this window's while it shows: hidden, it stops listening.
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                history.Voice?.Stop();
            }
        };

        // A conversation that was listed without its messages shows them as soon as they are read.
        history.MessagesLoaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ShowOpenConversation);
    }

    /// <summary>The menu the composer's plus opens.</summary>
    internal ContextMenu PlusMenu { get; }

    /// <summary>
    /// Brings the window forward, restored if it was minimized, and focuses the open conversation, or else the list. The
    /// first time, it opens centered on the monitor the user is working on.
    /// </summary>
    public void ShowAndActivate()
    {
        if (!_placed)
        {
            _placed = true;
            _placement.PlaceCenteredOnActiveMonitor(Handle, Width, Height, ScreenMargin);
        }

        _history.RefreshTimes();

        // The conversations saved since the window was last shown join the list, and the ones it lists take their latest
        // preview. It reads the database away from the UI thread.
        _ = _history.RefreshAsync();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Show();
        Activate();
        FocusContent();
    }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // Esc stops the answer that is coming, as it does in the floating conversation, and then puts the search away.
        if (e.Key == Key.Escape && _history.IsAnswering)
        {
            e.Handled = true;
            _history.Stop();
        }
        else if (e.Key == Key.Escape && _history.IsSearchOpen)
        {
            e.Handled = true;
            _history.CloseSearchCommand.Execute(null);
        }

        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnClosing(CancelEventArgs e)
    {
        // Its close button, Alt+F4 and the taskbar's Close window hide it. When the application shuts down, WPF
        // closes it regardless.
        if (!_closing)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    /// <summary>Closes the window instead of hiding it, for tests.</summary>
    internal void CloseForGood()
    {
        _closing = true;
        Close();
    }

    private void OnCloseExecuted(object sender, ExecutedRoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnMinimizeExecuted(object sender, ExecutedRoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            SystemCommands.RestoreWindow(this);
        }
        else
        {
            SystemCommands.MaximizeWindow(this);
        }
    }

    // Find (Ctrl+F) opens the search field, or moves the keyboard back into it when it is open.
    private void OnFindExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        _history.IsSearchOpen = true;
        FocusSearch();
    }

    private void OnShowViewMenuExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        ViewMenu.PlacementTarget = FilterButton;
        ViewMenu.IsOpen = true;
    }

    // Opens the conversation chosen in the list. A row moving to another section, such as when a day passes or its
    // conversation is opened again, leaves the list for a moment and loses its selection; the conversation stays open,
    // and its row is selected again once it is back. As in a sidebar, the open conversation's row stays selected.
    private void OnRowsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ConversationRows.SelectedItem is HistoryConversationViewModel chosen)
        {
            _history.Selected = chosen;
        }
        else if (_history.Selected is { } open)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
            {
                if (ReferenceEquals(_history.Selected, open) && ConversationRows.SelectedItem is null
                    && ConversationRows.Items.Contains(open))
                {
                    ConversationRows.SetCurrentValue(Selector.SelectedItemProperty, open);
                }
            });
        }
    }

    // Lists the conversations again after a search changed which of them the sidebar shows, in both views of the list, and
    // opens the open conversation's row or card again if it is back in the list.
    private void ListAgain()
    {
        // The lists lose their selection when they are made again, which must not close the open conversation.
        using (_history.HoldSelection())
        {
            (Resources["ConversationSections"] as CollectionViewSource)?.View?.Refresh();
            (Resources["ConversationCards"] as CollectionViewSource)?.View?.Refresh();
        }

        // Once the lists have made their rows and cards again, the open one's is selected again. A list that was made
        // again can keep its selected item without having selected the item's card, so it is selected by its place.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_history.Selected is not { } open)
            {
                return;
            }

            using (_history.HoldSelection())
            {
                foreach (var list in new[] { Conversations, ConversationRows })
                {
                    if (list.Items.IndexOf(open) is >= 0 and var index && list.SelectedIndex < 0)
                    {
                        // By place, which selects it even when the list still holds it as its selected item.
                        list.SetCurrentValue(Selector.SelectedIndexProperty, index);
                    }
                }
            }
        });
    }

    // Keeps a conversation the search leaves out of the sidebar from being listed.
    private void OnFilterConversations(object sender, FilterEventArgs e) =>
        e.Accepted = e.Item is HistoryConversationViewModel { IsShown: true };

    // Puts the keyboard in the search field once it is showing.
    private void FocusSearch() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_history.IsSearchOpen)
            {
                SearchInput.FocusInput();
            }
        });

    private void OnHistoryChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HistoryViewModel.IsSearchOpen))
        {
            if (_history.IsSearchOpen)
            {
                FocusSearch();
            }
            else if (IsActive)
            {
                // The search is put away: the keyboard goes back to where it was working.
                Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusContent);
            }
        }
        else if (e.PropertyName == nameof(HistoryViewModel.Selected))
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ShowOpenConversation);
        }
        else if (e.PropertyName == nameof(HistoryViewModel.Layout))
        {
            // The other view of the list comes up showing the open conversation, and keeps the keyboard in the list.
            var hadFocus = Conversations.IsKeyboardFocusWithin || ConversationRows.IsKeyboardFocusWithin;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                var list = VisibleList;
                if (_history.Selected is { } selected)
                {
                    list.ScrollIntoView(selected);
                }

                if (hadFocus)
                {
                    FocusList(list);
                }
            });
        }
    }

    // A conversation opens at its newest message, or, when it was opened from a search result, at the message that matched.
    private void ShowOpenConversation()
    {
        if (_history is { IsSearching: true, Selected.MatchedMessageId: { } matched }
            && _history.Selected.Messages.FirstOrDefault(message => message.Id == matched) is { } match
            && Conversation.ScrollTo(match))
        {
            return;
        }

        Conversation.ScrollToEnd();
    }

    private ListBox VisibleList => _history.IsList ? ConversationRows : Conversations;

    // Attaches what was chosen from the plus's menu and hands the keyboard to the composer, to ask about it.
    private void AttachAndCompose(Action attach)
    {
        attach();
        Activate();
        ComposerInput.FocusInputAtEnd();
    }

    private void FocusContent()
    {
        if (_history.HasSelection)
        {
            Conversation.Focus();
        }
        else
        {
            FocusList(VisibleList);
        }
    }

    // Focuses the open conversation's row or card, if it has been created, and otherwise the list itself.
    private static void FocusList(ListBox list)
    {
        if (list.SelectedItem is not { } selected
            || list.ItemContainerGenerator.ContainerFromItem(selected) is not UIElement container
            || !container.Focus())
        {
            list.Focus();
        }
    }

    // A maximized window reaches past the screen by its resize border on every side; its content stays inside.
    private void KeepContentOnScreen()
    {
        if (WindowState == WindowState.Maximized)
        {
            var overhang = FrameMetrics.MaximizedOverhang(Handle) / VisualTreeHelper.GetDpi(this).DpiScaleX;
            Root.Margin = new Thickness(overhang);
        }
        else
        {
            Root.Margin = default;
        }
    }

    private nint Handle => new WindowInteropHelper(this).EnsureHandle();
}
