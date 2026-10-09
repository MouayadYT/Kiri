using System.Windows;
using System.Windows.Interop;
using Assistant.UI.ViewModels;
using Assistant.UI.Views;
using Assistant.Windows.Placement;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    [Fact]
    public void HiddenOverlayIsPlacedByItsPillBeforeShowingAndVisibleOverlayStaysPut() => RunSta(() =>
    {
        var app = Application.Current;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
        });
        var placement = new FakePlacement();
        var window = CreateAssistant(placement).Window;
        window.ShowActivated = false;
        bool? visibleWhenPlaced = null;
        placement.OnPlace = () => visibleWhenPlaced = window.IsVisible;
        try
        {
            window.ShowAndFocus();
            Pump();
            var (handle, layout) = Assert.Single(placement.Calls);
            Assert.False(visibleWhenPlaced);
            Assert.True(window.IsVisible);
            Assert.Equal(new WindowInteropHelper(window).Handle, handle);

            // The pill inside its shadow gutter, 22 % down the work area; the window really is that size.
            Assert.Equal(new OverlayLayout(610, 183, 45, 28, 520, 91, 0.22), layout);
            Assert.Equal(layout.Width, window.ActualWidth);
            Assert.Equal(layout.Height, window.ActualHeight);

            // Refocusing a visible overlay never moves it.
            window.ShowAndFocus();
            Assert.Single(placement.Calls);

            // Every reappearance is placed again, on the same window.
            window.Hide();
            window.ShowAndFocus();
            Assert.Equal(2, placement.Calls.Count);
            Assert.Equal(handle, placement.Calls[1].Window);
            Assert.False(visibleWhenPlaced);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Clear();
        }
    });
}
