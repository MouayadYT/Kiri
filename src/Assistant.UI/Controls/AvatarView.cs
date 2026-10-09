using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Controls;

/// <summary>
/// Where one small disc of a group avatar sits on the larger disc, in units of a disc 36 across: its center, from the
/// larger disc's center, and its radius.
/// </summary>
public readonly record struct AvatarSlot(double X, double Y, double Radius);

/// <summary>
/// The arrangements of a group avatar. Seven is measured from the Messages reference: the first person's photo large
/// at the upper left, the rest around it, getting smaller. Fewer people sit larger, in arrangements of their own.
/// </summary>
public static class AvatarLayout
{
    /// <summary>The most people an avatar shows.</summary>
    public const int MaxParticipants = 7;

    private static readonly AvatarSlot[] Reference =
    [
        new(-5.0, -5.1, 8.1), new(8.9, -6.8, 3.9), new(8.1, 4.4, 5.5), new(-8.5, 7.9, 3.6),
        new(-1.2, 6.1, 1.75), new(0.9, 12.25, 2.6), new(3.75, -12.8, 1.6),
    ];

    private static readonly AvatarSlot[][] Small =
    [
        [],
        [new(0, 0, 18)],
        [new(-5.2, -4.6, 9.0), new(6.8, 6.4, 7.0)],
        [new(-6.0, -5.2, 8.0), new(8.0, -3.5, 5.6), new(0.5, 9.5, 6.0)],
        [new(-6.0, -6.0, 7.2), new(8.5, -4.0, 5.2), new(-6.5, 8.0, 5.0), new(6.5, 8.5, 6.0)],
    ];

    /// <summary>The slots for <paramref name="count"/> people, the first for the most important.</summary>
    public static IReadOnlyList<AvatarSlot> For(int count) =>
        count <= 0 ? [] : count < Small.Length ? Small[count] : Reference[..Math.Min(count, MaxParticipants)];
}

/// <summary>
/// The avatar of the people in a conversation (PROJECT_SPEC §4.1): one person is a disc of their own, and several are a
/// cluster of small discs on a larger, translucent one. A person is a photo, initials on a colored disc, or, with
/// neither, a silhouette. It fills the square it is given, and draws itself, so any number of them stay cheap.
/// </summary>
public sealed class AvatarView : FrameworkElement
{
    // The layouts are drawn on a disc 36 across, and scaled to the one given.
    private const double ReferenceDiameter = 36;

    /// <summary>Identifies the <see cref="Participants"/> property.</summary>
    public static readonly DependencyProperty ParticipantsProperty = DependencyProperty.Register(
        nameof(Participants), typeof(IReadOnlyList<AvatarParticipant>), typeof(AvatarView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="ClusterBrush"/> property.</summary>
    public static readonly DependencyProperty ClusterBrushProperty = RegisterBrush(nameof(ClusterBrush));

    /// <summary>Identifies the <see cref="InitialsBrush"/> property.</summary>
    public static readonly DependencyProperty InitialsBrushProperty = RegisterBrush(nameof(InitialsBrush));

    /// <summary>Identifies the <see cref="Foreground"/> property.</summary>
    public static readonly DependencyProperty ForegroundProperty = TextElement.ForegroundProperty.AddOwner(
        typeof(AvatarView), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Identifies the <see cref="FontFamily"/> property.</summary>
    public static readonly DependencyProperty FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner(
        typeof(AvatarView), new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsRender));

    static AvatarView()
    {
        // An avatar is only a picture: nothing on it takes the mouse, and it has no size of its own.
        IsHitTestVisibleProperty.OverrideMetadata(typeof(AvatarView), new UIPropertyMetadata(false));
    }

    /// <summary>The people, most important first; more than <see cref="AvatarLayout.MaxParticipants"/> are not drawn.</summary>
    public IReadOnlyList<AvatarParticipant>? Participants
    {
        get => (IReadOnlyList<AvatarParticipant>?)GetValue(ParticipantsProperty);
        set => SetValue(ParticipantsProperty, value);
    }

    /// <summary>The translucent disc a group's people sit on.</summary>
    public Brush? ClusterBrush { get => (Brush?)GetValue(ClusterBrushProperty); set => SetValue(ClusterBrushProperty, value); }

    /// <summary>The disc a person's initials, or silhouette, are drawn on.</summary>
    public Brush? InitialsBrush { get => (Brush?)GetValue(InitialsBrushProperty); set => SetValue(InitialsBrushProperty, value); }

    /// <summary>The color of initials and of a silhouette.</summary>
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    /// <summary>The typeface of initials.</summary>
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        var people = Participants;
        var diameter = Math.Min(ActualWidth, ActualHeight);
        if (people is not { Count: > 0 } || diameter <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var count = Math.Min(people.Count, AvatarLayout.MaxParticipants);
        if (count == 1)
        {
            DrawPerson(context, people[0], center, diameter / 2);
            return;
        }

        context.DrawEllipse(ClusterBrush, null, center, diameter / 2, diameter / 2);
        var scale = diameter / ReferenceDiameter;
        var slots = AvatarLayout.For(count);
        for (var i = 0; i < count; i++)
        {
            var slot = slots[i];
            DrawPerson(context, people[i], new Point(center.X + (slot.X * scale), center.Y + (slot.Y * scale)),
                slot.Radius * scale);
        }
    }

    private void DrawPerson(DrawingContext context, AvatarParticipant person, Point center, double radius)
    {
        if (person.Photo is { } photo)
        {
            context.DrawEllipse(new ImageBrush(photo) { Stretch = Stretch.UniformToFill }, null, center, radius, radius);
            return;
        }

        context.DrawEllipse(InitialsBrush, null, center, radius, radius);
        if (!string.IsNullOrWhiteSpace(person.Initials))
        {
            DrawInitials(context, person.Initials.Trim(), center, radius);
        }
        else
        {
            DrawSilhouette(context, center, radius);
        }
    }

    // One letter fills a disc more than two do. Even the smallest disc gets its letters, as in the reference.
    private void DrawInitials(DrawingContext context, string initials, Point center, double radius)
    {
        var size = radius * (initials.Length == 1 ? 1.15 : 0.9);
        if (size < 1.4)
        {
            return;
        }

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var text = new FormattedText(initials, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size,
            Foreground, VisualTreeHelper.GetDpi(this).PixelsPerDip);

        // Centered on the capitals, not on the line, which reaches below the baseline.
        context.DrawText(text, new Point(center.X - (text.WidthIncludingTrailingWhitespace / 2), center.Y - text.Baseline + (0.35 * size)));
    }

    private void DrawSilhouette(DrawingContext context, Point center, double radius)
    {
        context.PushClip(new EllipseGeometry(center, radius, radius));
        context.DrawEllipse(Foreground, null, new Point(center.X, center.Y - (0.2 * radius)), 0.3 * radius, 0.3 * radius);
        context.DrawEllipse(Foreground, null, new Point(center.X, center.Y + (0.95 * radius)), 0.62 * radius, 0.6 * radius);
        context.Pop();
    }

    private static DependencyProperty RegisterBrush(string name) => DependencyProperty.Register(
        name, typeof(Brush), typeof(AvatarView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
}
