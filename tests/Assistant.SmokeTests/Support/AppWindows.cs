using Assistant.UI.Views;

namespace Assistant.SmokeTests.Support;

/// <summary>Putting the app's windows away at the end of a check.</summary>
internal static class AppWindows
{
    /// <summary>
    /// Hides the Assistant's window at once and closes it once its show and hide animations have nothing left to draw: a window closed in the middle of
    /// one is drawn into once more by a frame that was already on its way, which the app never meets (its window closes only as the app ends).
    /// </summary>
    public static async Task CloseAsync(AssistantWindow window)
    {
        window.HideNow();
        await Task.Delay(300);
        window.Close();
    }
}
