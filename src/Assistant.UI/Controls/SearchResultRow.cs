using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Controls;

/// <summary>
/// One row of a list of search results (PROJECT_SPEC §4.1), for any kind of result: an icon at the left, a title, an
/// optional subtitle under it, an optional note at the right such as a date, optional badges on the icon's corner, and,
/// while the row is highlighted, the action it offers with the key that does it. The look is in
/// <c>Themes/Controls/SearchResults.xaml</c>. The row is never focused: the bar's editor keeps the keyboard. Clicking it
/// runs its <see cref="Command"/>.
/// </summary>
public sealed class SearchResultRow : Control
{
    /// <summary>Identifies the <see cref="Kind"/> property.</summary>
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(SearchResultKind), typeof(SearchResultRow),
        new FrameworkPropertyMetadata(SearchResultKind.App, OnKindChanged));

    /// <summary>Identifies the <see cref="Icon"/> property.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(SearchResultIcon), typeof(SearchResultRow),
        new FrameworkPropertyMetadata(null, null, (row, value) =>
            value ?? SearchResultIcon.ForKind(((SearchResultRow)row).Kind)));

    /// <summary>Identifies the <see cref="Title"/> property.</summary>
    public static readonly DependencyProperty TitleProperty = RegisterText(nameof(Title));

    /// <summary>Identifies the <see cref="Subtitle"/> property.</summary>
    public static readonly DependencyProperty SubtitleProperty = RegisterText(nameof(Subtitle));

    /// <summary>Identifies the <see cref="Detail"/> property.</summary>
    public static readonly DependencyProperty DetailProperty = RegisterText(nameof(Detail));

    /// <summary>Identifies the <see cref="ActionText"/> property.</summary>
    public static readonly DependencyProperty ActionTextProperty = RegisterText(nameof(ActionText));

    /// <summary>Identifies the <see cref="ActionKey"/> property.</summary>
    public static readonly DependencyProperty ActionKeyProperty = RegisterText(nameof(ActionKey));

    /// <summary>Identifies the <see cref="SecondaryActionText"/> property.</summary>
    public static readonly DependencyProperty SecondaryActionTextProperty = RegisterText(nameof(SecondaryActionText));

    /// <summary>Identifies the <see cref="SecondaryActionKey"/> property.</summary>
    public static readonly DependencyProperty SecondaryActionKeyProperty = RegisterText(nameof(SecondaryActionKey));

    /// <summary>Identifies the <see cref="Badges"/> property.</summary>
    public static readonly DependencyProperty BadgesProperty = DependencyProperty.Register(
        nameof(Badges), typeof(IEnumerable<SearchResultBadge>), typeof(SearchResultRow),
        new FrameworkPropertyMetadata(null));

    /// <summary>Identifies the <see cref="Size"/> property.</summary>
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(SearchResultRowSize), typeof(SearchResultRow),
        new FrameworkPropertyMetadata(SearchResultRowSize.Standard));

    /// <summary>Identifies the <see cref="IsSelected"/> property.</summary>
    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(SearchResultRow),
        new FrameworkPropertyMetadata(false, OnIsSelectedChanged));

    /// <summary>Identifies the <see cref="Command"/> property.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(SearchResultRow), new FrameworkPropertyMetadata(null));

    static SearchResultRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SearchResultRow),
            new FrameworkPropertyMetadata(typeof(SearchResultRow)));
        FocusableProperty.OverrideMetadata(typeof(SearchResultRow), new FrameworkPropertyMetadata(false));
    }

    public SearchResultRow()
    {
        // An icon that was never set is the kind's own.
        CoerceValue(IconProperty);
    }

    /// <summary>What the result is. It decides the icon the row draws when it is given none.</summary>
    public SearchResultKind Kind { get => (SearchResultKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    /// <summary>The picture at the left. Given none, the row has the kind's own.</summary>
    public SearchResultIcon Icon { get => (SearchResultIcon)GetValue(IconProperty); set => SetValue(IconProperty, value); }

    /// <summary>The result's name.</summary>
    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    /// <summary>A second line under the name; empty for none.</summary>
    public string Subtitle { get => (string)GetValue(SubtitleProperty); set => SetValue(SubtitleProperty, value); }

    /// <summary>A note at the right of the name, such as a date; empty for none.</summary>
    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    /// <summary>The words of the action the row offers while it is highlighted; empty for none.</summary>
    public string ActionText { get => (string)GetValue(ActionTextProperty); set => SetValue(ActionTextProperty, value); }

    /// <summary>The key that does that action, as written on its cap; empty for none.</summary>
    public string ActionKey { get => (string)GetValue(ActionKeyProperty); set => SetValue(ActionKeyProperty, value); }

    /// <summary>The words of what else the row offers while it is highlighted ("Actions"); empty for none.</summary>
    public string SecondaryActionText { get => (string)GetValue(SecondaryActionTextProperty); set => SetValue(SecondaryActionTextProperty, value); }

    /// <summary>The key that does it, as written on its cap; empty for none.</summary>
    public string SecondaryActionKey { get => (string)GetValue(SecondaryActionKeyProperty); set => SetValue(SecondaryActionKeyProperty, value); }

    /// <summary>The marks on the icon's lower right corner.</summary>
    public IEnumerable<SearchResultBadge>? Badges
    {
        get => (IEnumerable<SearchResultBadge>?)GetValue(BadgesProperty);
        set => SetValue(BadgesProperty, value);
    }

    /// <summary>How much room the row gives its icon.</summary>
    public SearchResultRowSize Size { get => (SearchResultRowSize)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>Whether the row is the highlighted one. A highlighted row scrolls into view and shows its action.</summary>
    public bool IsSelected { get => (bool)GetValue(IsSelectedProperty); set => SetValue(IsSelectedProperty, value); }

    /// <summary>What the row does when it is clicked.</summary>
    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }

    /// <summary>Runs <see cref="Command"/>, if the row has one and it can run.</summary>
    public void Activate()
    {
        if (Command is { } command && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!IsMouseCaptured)
        {
            return;
        }

        ReleaseMouseCapture();
        e.Handled = true;

        // Released over the row, not dragged off it.
        if (IsMouseOver)
        {
            Activate();
        }
    }

    // The right mouse button is the row's own: pressed and let go over it, it opens the row's menu of what else the result can do. The row opens the menu
    // itself and does not leave it to the system's lookup of what the pointer is over, which is only as fresh as the pointer's last move: the list appears
    // under a pointer that has not moved, and the first rows are the ones it appears under.
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (HasMenu)
        {
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        if (OpenMenu())
        {
            e.Handled = true;
        }
    }

    /// <summary>Whether the row has a menu of other things to do, and it may be opened.</summary>
    public bool HasMenu => ContextMenu is not null && IsEnabled && ContextMenuService.GetIsEnabled(this);

    /// <summary>Opens the row's menu where the pointer is. Returns whether it has one to open.</summary>
    public bool OpenMenu()
    {
        if (!HasMenu || ContextMenu is not { } menu)
        {
            return false;
        }

        menu.PlacementTarget = this;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
        return true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new RowPeer(this);

    private static DependencyProperty RegisterText(string name) => DependencyProperty.Register(
        name, typeof(string), typeof(SearchResultRow),
        new FrameworkPropertyMetadata("", null, (_, value) => value ?? ""));

    // A kind that has no icon of its own to draw is drawn with the kind's, which is how an unset icon is resolved.
    private static void OnKindChanged(DependencyObject row, DependencyPropertyChangedEventArgs e) =>
        row.CoerceValue(IconProperty);

    private static void OnIsSelectedChanged(DependencyObject row, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue)
        {
            ((SearchResultRow)row).BringIntoView();
        }
    }

    // A list item that can be invoked, so assistive technology can run a result as a click does.
    private sealed class RowPeer(SearchResultRow row) : FrameworkElementAutomationPeer(row), IInvokeProvider
    {
        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        public void Invoke() => row.Activate();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

        protected override string GetClassNameCore() => nameof(SearchResultRow);
    }
}
