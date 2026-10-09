namespace Assistant.UI.Windowing;

/// <summary>The History window, the Assistant's full window, as whatever opens it sees it.</summary>
internal interface IHistoryWindow
{
    /// <summary>
    /// Brings the window forward, restored if it was minimized, and focuses it. The first time, it opens on the monitor
    /// the user is working on.
    /// </summary>
    void ShowAndActivate();
}
