using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Assistant.UI.Controls;

/// <summary>
/// Lays an items control's items out in <see cref="Columns"/> equal columns across the full width, taking turns: the
/// first item goes in the first column, the next in the second, and so on, each <see cref="ItemHeight"/> tall and
/// stacked under the item before it in its column. Each column starts <see cref="Stagger"/> lower than the one to its
/// left, as the History window's conversation cards do in its reference.
/// </summary>
/// <remarks>
/// The panel scrolls its items itself and creates containers only for the items in view, and a little beyond, so a
/// list of thousands of items stays as quick as a list of a few. As every item is the same height, where each one lies
/// is known without measuring the others. <see cref="Padding"/> lies inside the scrolled area, so items pass through
/// it; an item brought into view is kept clear of it.
/// </remarks>
public sealed class VirtualizingStaggeredColumnsPanel : VirtualizingPanel, IScrollInfo
{
    /// <summary>Identifies the <see cref="Columns"/> property.</summary>
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(VirtualizingStaggeredColumnsPanel),
        new FrameworkPropertyMetadata(2, FrameworkPropertyMetadataOptions.AffectsMeasure), value => value is int and > 0);

    /// <summary>Identifies the <see cref="ColumnSpacing"/> property.</summary>
    public static readonly DependencyProperty ColumnSpacingProperty = DependencyProperty.Register(
        nameof(ColumnSpacing), typeof(double), typeof(VirtualizingStaggeredColumnsPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsLength);

    /// <summary>Identifies the <see cref="RowSpacing"/> property.</summary>
    public static readonly DependencyProperty RowSpacingProperty = DependencyProperty.Register(
        nameof(RowSpacing), typeof(double), typeof(VirtualizingStaggeredColumnsPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsLength);

    /// <summary>Identifies the <see cref="Stagger"/> property.</summary>
    public static readonly DependencyProperty StaggerProperty = DependencyProperty.Register(
        nameof(Stagger), typeof(double), typeof(VirtualizingStaggeredColumnsPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsLength);

    /// <summary>Identifies the <see cref="ItemHeight"/> property.</summary>
    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(VirtualizingStaggeredColumnsPanel),
        new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsMeasure), IsLength);

    /// <summary>Identifies the <see cref="Padding"/> property.</summary>
    public static readonly DependencyProperty PaddingProperty = DependencyProperty.Register(
        nameof(Padding), typeof(Thickness), typeof(VirtualizingStaggeredColumnsPanel),
        new FrameworkPropertyMetadata(default(Thickness), FrameworkPropertyMetadataOptions.AffectsMeasure));

    // How wide a column is when nothing limits the panel's width.
    private const double UnconstrainedColumnWidth = 140;

    // How far a mouse wheel notch or an arrow on a scroll bar moves, as WPF's own panels do.
    private const double LineHeight = 16;

    private Size _extent;
    private Size _viewport;
    private double _offset;

    /// <summary>How many columns there are.</summary>
    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>The gap between neighboring columns, in DIPs.</summary>
    public double ColumnSpacing
    {
        get => (double)GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The gap between items stacked in a column, in DIPs.</summary>
    public double RowSpacing
    {
        get => (double)GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    /// <summary>How much lower each column starts than the one to its left, in DIPs.</summary>
    public double Stagger
    {
        get => (double)GetValue(StaggerProperty);
        set => SetValue(StaggerProperty, value);
    }

    /// <summary>How tall every item is, in DIPs.</summary>
    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    /// <summary>The space around the items, inside the scrolled area, in DIPs.</summary>
    public Thickness Padding
    {
        get => (Thickness)GetValue(PaddingProperty);
        set => SetValue(PaddingProperty, value);
    }

    /// <inheritdoc/>
    public bool CanVerticallyScroll { get; set; }

    /// <inheritdoc/>
    public bool CanHorizontallyScroll { get; set; }

    /// <inheritdoc/>
    public double ExtentWidth => _extent.Width;

    /// <inheritdoc/>
    public double ExtentHeight => _extent.Height;

    /// <inheritdoc/>
    public double ViewportWidth => _viewport.Width;

    /// <inheritdoc/>
    public double ViewportHeight => _viewport.Height;

    /// <inheritdoc/>
    public double HorizontalOffset => 0;

    /// <inheritdoc/>
    public double VerticalOffset => _offset;

    /// <inheritdoc/>
    public ScrollViewer? ScrollOwner { get; set; }

    /// <summary>The width of a column when the panel is <paramref name="width"/> DIPs wide.</summary>
    public double ColumnWidth(double width) =>
        Math.Max(0, (width - Padding.Left - Padding.Right - (Columns - 1) * ColumnSpacing) / Columns);

    /// <summary>
    /// Where the item at <paramref name="index"/> lies in a panel <paramref name="width"/> DIPs wide, measured from the
    /// top of the scrolled area.
    /// </summary>
    public Rect SlotOf(int index, double width)
    {
        var column = index % Columns;
        var row = index / Columns;
        var columnWidth = ColumnWidth(width);
        return new Rect(
            Padding.Left + column * (columnWidth + ColumnSpacing),
            Padding.Top + column * Stagger + row * (ItemHeight + RowSpacing),
            columnWidth,
            ItemHeight);
    }

    /// <summary>How tall the scrolled area is with <paramref name="count"/> items.</summary>
    public double ExtentHeightFor(int count)
    {
        var bottom = 0.0;
        for (var column = 0; column < Math.Min(Columns, count); column++)
        {
            var rows = (count - column + Columns - 1) / Columns;
            bottom = Math.Max(bottom, column * Stagger + rows * (ItemHeight + RowSpacing) - RowSpacing);
        }

        return Padding.Top + bottom + Padding.Bottom;
    }

    /// <inheritdoc/>
    public void LineUp() => SetVerticalOffset(_offset - LineHeight);

    /// <inheritdoc/>
    public void LineDown() => SetVerticalOffset(_offset + LineHeight);

    /// <inheritdoc/>
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);

    /// <inheritdoc/>
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);

    /// <inheritdoc/>
    public void MouseWheelUp() => SetVerticalOffset(_offset - SystemParameters.WheelScrollLines * LineHeight);

    /// <inheritdoc/>
    public void MouseWheelDown() => SetVerticalOffset(_offset + SystemParameters.WheelScrollLines * LineHeight);

    /// <inheritdoc/>
    public void LineLeft() { }

    /// <inheritdoc/>
    public void LineRight() { }

    /// <inheritdoc/>
    public void PageLeft() { }

    /// <inheritdoc/>
    public void PageRight() { }

    /// <inheritdoc/>
    public void MouseWheelLeft() { }

    /// <inheritdoc/>
    public void MouseWheelRight() { }

    /// <inheritdoc/>
    public void SetHorizontalOffset(double offset) { }

    /// <inheritdoc/>
    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (double.IsNaN(offset) || offset == _offset)
        {
            return;
        }

        _offset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    /// <inheritdoc/>
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        if (rectangle.IsEmpty || visual is null || visual == this || !IsAncestorOf(visual))
        {
            return Rect.Empty;
        }

        var bounds = visual.TransformToAncestor(this).TransformBounds(rectangle);
        var before = _offset;
        ScrollToShow(bounds.Top + _offset, bounds.Bottom + _offset);
        bounds.Offset(0, before - _offset);
        return bounds;
    }

    /// <inheritdoc/>
    protected override void BringIndexIntoView(int index)
    {
        if (index < 0 || index >= ItemCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        var slot = SlotOf(index, _viewport.Width);
        ScrollToShow(slot.Top, slot.Bottom);
        UpdateLayout();
    }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width)
            ? Padding.Left + Padding.Right + Columns * UnconstrainedColumnWidth + (Columns - 1) * ColumnSpacing
            : availableSize.Width;
        var count = ItemCount;
        var extent = new Size(width, ExtentHeightFor(count));
        var viewport = new Size(width, double.IsInfinity(availableSize.Height) ? extent.Height : availableSize.Height);
        UpdateScrollInfo(extent, viewport);

        // The items in view and one item's height beyond, so a little scrolling needs no new containers.
        var (first, last) = RangeIn(_offset - ItemHeight, _offset + viewport.Height + ItemHeight, count);
        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        if (generator is not null && first <= last)
        {
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, allowStartAtRealizedItem: true))
            {
                for (var index = first; index <= last; index++, childIndex++)
                {
                    var child = (UIElement)generator.GenerateNext(out var isNewlyRealized);
                    if (childIndex >= children.Count || children[childIndex] != child)
                    {
                        // A new container, or a recycled one, which must be filled with its item again.
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    else if (isNewlyRealized)
                    {
                        generator.PrepareItemContainer(child);
                    }

                    child.Measure(SlotOf(index, width).Size);
                }
            }
        }

        RemoveContainersOutside(first, last);
        return new Size(width, viewport.Height);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        var generator = ItemContainerGenerator;
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = generator?.IndexFromGeneratorPosition(new GeneratorPosition(i, 0)) ?? i;
            var slot = SlotOf(index, finalSize.Width);
            slot.Offset(0, -_offset);
            InternalChildren[i].Arrange(slot);
        }

        return finalSize;
    }

    /// <inheritdoc/>
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        // The generator has let go of these containers; the panel's children must follow.
        switch (args.Action)
        {
            case NotifyCollectionChangedAction.Remove:
            case NotifyCollectionChangedAction.Replace:
                RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
                break;
            case NotifyCollectionChangedAction.Move:
                RemoveInternalChildRange(args.OldPosition.Index, args.ItemUICount);
                break;
        }

        base.OnItemsChanged(sender, args);
    }

    /// <inheritdoc/>
    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        SetVerticalOffset(0);
    }

    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    // The first and last index of the items that reach into the band from top to bottom of the scrolled area.
    private (int First, int Last) RangeIn(double top, double bottom, int count)
    {
        if (count == 0)
        {
            return (0, -1);
        }

        var step = ItemHeight + RowSpacing;
        int? firstRow = null;
        var lastRow = -1;
        for (var column = 0; column < Math.Min(Columns, count); column++)
        {
            var start = Padding.Top + column * Stagger;
            var from = step > 0 ? (int)Math.Floor((top - start - ItemHeight) / step) + 1 : 0;
            var to = step > 0 ? (int)Math.Ceiling((bottom - start) / step) - 1 : int.MaxValue / Columns - 1;
            from = Math.Max(0, from);
            if (from <= to)
            {
                firstRow = Math.Min(firstRow ?? from, from);
                lastRow = Math.Max(lastRow, to);
            }
        }

        if (firstRow is not { } row)
        {
            return (0, -1);
        }

        var first = (int)Math.Min((long)row * Columns, count);
        var last = (int)Math.Min((long)lastRow * Columns + Columns - 1, count - 1);
        return (first, last);
    }

    // Lets go of the containers of items outside the range, except one that has keyboard focus, which would otherwise
    // be lost.
    private void RemoveContainersOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        if (generator is null)
        {
            return;
        }

        var recycle = VirtualizingPanel.GetVirtualizationMode(ItemsControl.GetItemsOwner(this) ?? (DependencyObject)this)
            == VirtualizationMode.Recycling;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if ((index >= first && index <= last) || InternalChildren[i].IsKeyboardFocusWithin)
            {
                continue;
            }

            if (recycle && generator is IRecyclingItemContainerGenerator recycling)
            {
                recycling.Recycle(position, 1);
            }
            else
            {
                generator.Remove(position, 1);
            }

            RemoveInternalChildRange(i, 1);
        }
    }

    // Scrolls as little as it takes to show the band from top to bottom of the scrolled area clear of the padding.
    private void ScrollToShow(double top, double bottom)
    {
        var offset = _offset;
        if (bottom + Padding.Bottom > offset + _viewport.Height)
        {
            offset = bottom + Padding.Bottom - _viewport.Height;
        }

        if (top - Padding.Top < offset)
        {
            offset = top - Padding.Top;
        }

        SetVerticalOffset(offset);
    }

    private void UpdateScrollInfo(Size extent, Size viewport)
    {
        if (extent == _extent && viewport == _viewport)
        {
            return;
        }

        _extent = extent;
        _viewport = viewport;
        _offset = Math.Clamp(_offset, 0, Math.Max(0, extent.Height - viewport.Height));
        ScrollOwner?.InvalidateScrollInfo();
    }

    private static bool IsLength(object value) => value is double length && length >= 0 && double.IsFinite(length);
}
