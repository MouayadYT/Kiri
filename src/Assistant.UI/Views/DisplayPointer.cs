using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Assistant.Core.Displays;
using Assistant.Windows.Displays;

namespace Assistant.UI.Views;

/// <summary>
/// Shows the user which display is meant, on the display itself, the way Windows' own "Identify" does: an outline round the whole display and a few
/// words in its middle, for a few seconds. It is a mark and nothing more. It never takes the keyboard, a click goes through it, and it goes by itself,
/// so that answering the question it goes with is all the user has to do.
/// </summary>
public sealed class DisplayPointer : IDisplayPointer
{
    /// <summary>How long the mark stays when nothing takes it away sooner.</summary>
    internal static readonly TimeSpan Stay = TimeSpan.FromSeconds(8);

    private Window? _mark;
    private DispatcherTimer? _timer;

    /// <inheritdoc/>
    public void PointOut(DisplayInfo display, string words)
    {
        ArgumentNullException.ThrowIfNull(display);
        OnTheWindowsThread(() => Show(display, words));
    }

    /// <inheritdoc/>
    public void Clear() => OnTheWindowsThread(Close);

    private static void OnTheWindowsThread(Action action)
    {
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void Show(DisplayInfo display, string words)
    {
        Close();
        var mark = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Focusable = false,
            IsHitTestVisible = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Content = Build(display, words),
        };

        // The window is laid over the display in the display's own pixels, before it is shown, so that it never appears anywhere else.
        var handle = new WindowInteropHelper(mark).EnsureHandle();
        if (!WindowsDisplays.Cover(handle, display))
        {
            mark.Close();
            return;
        }

        mark.Show();
        WindowsDisplays.Cover(handle, display);
        _mark = mark;
        _timer = new DispatcherTimer(Stay, DispatcherPriority.Background, (_, _) => Close(), mark.Dispatcher);
        _timer.Start();
    }

    private void Close()
    {
        _timer?.Stop();
        _timer = null;
        var mark = _mark;
        _mark = null;
        mark?.Close();
    }

    private static UIElement Build(DisplayInfo display, string words)
    {
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(24),
            Padding = new Thickness(44, 30, 44, 32),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = display.Name,
                        Foreground = Brushes.White,
                        FontSize = 54,
                        FontWeight = FontWeights.SemiBold,
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text = words,
                        Foreground = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
                        FontSize = 22,
                        Margin = new Thickness(0, 8, 0, 0),
                        HorizontalAlignment = HorizontalAlignment.Center,
                    },
                },
            },
        };

        return new Grid
        {
            Children =
            {
                new Border
                {
                    BorderBrush = new SolidColorBrush(Color.FromArgb(0xE0, 0x4C, 0xA6, 0xFF)),
                    BorderThickness = new Thickness(6),
                },
                card,
            },
        };
    }
}
