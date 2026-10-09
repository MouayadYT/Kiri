using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Assistant.UI.Controls;

/// <summary>
/// The chips above a composer (PROJECT_SPEC §4.2): its chips (<see cref="Assistant.UI.Messages.AttachmentChip"/>) side by side,
/// wrapping onto a second line when they do not fit one, and never onto a third: beyond two lines the chips scroll (the mouse wheel
/// moves them, with no scroll bar), fading at the edge that has more beyond it, and the newest chip is brought into view as it is
/// attached. So any number of attachments leaves the composer at most two chips tall. It takes up no room while it is empty. Its
/// template is in Themes/Controls/Conversation.xaml.
/// </summary>
public sealed class AttachmentChipList : ItemsControl
{
    // How far from an edge that has more beyond it the chips fade out, in DIPs.
    private const double FadeHeight = 14;

    // How far the wheel moves the chips: this much of a notch's 120 units.
    private const double WheelScale = 0.5;

    private ScrollViewer? _scroller;

    /// <inheritdoc/>
    public override void OnApplyTemplate()
    {
        if (_scroller is { } old)
        {
            old.ScrollChanged -= OnScrollChanged;
        }

        base.OnApplyTemplate();
        _scroller = GetTemplateChild("PART_Scroller") as ScrollViewer;
        if (_scroller is not null)
        {
            _scroller.ScrollChanged += OnScrollChanged;
        }
    }

    /// <inheritdoc/>
    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            // The new chip is at the end: once it is laid out, the chips show it.
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => _scroller?.ScrollToBottom());
        }
    }

    /// <inheritdoc/>
    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (_scroller is { ScrollableHeight: > 0 } scroller)
        {
            scroller.ScrollToVerticalOffset(scroller.VerticalOffset - (e.Delta * WheelScale));
            e.Handled = true;
        }
    }

    // The chips fade toward each side that has more beyond it, and not at all when they all fit.
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_scroller is not { } scroller)
        {
            return;
        }

        var height = scroller.ViewportHeight;
        if (scroller.ScrollableHeight <= 0 || height <= 0)
        {
            scroller.OpacityMask = null;
            return;
        }

        var fade = Math.Min(FadeHeight / height, 0.5);
        var atStart = scroller.VerticalOffset <= 0.5;
        var atEnd = scroller.VerticalOffset >= scroller.ScrollableHeight - 0.5;
        var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        mask.GradientStops.Add(new GradientStop(atStart ? Colors.Black : Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, atStart ? 0 : fade));
        mask.GradientStops.Add(new GradientStop(Colors.Black, atEnd ? 1 : 1 - fade));
        mask.GradientStops.Add(new GradientStop(atEnd ? Colors.Black : Colors.Transparent, 1));
        mask.Freeze();
        scroller.OpacityMask = mask;
    }
}
