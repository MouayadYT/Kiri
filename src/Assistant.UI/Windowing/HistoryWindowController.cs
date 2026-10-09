using Assistant.UI.ViewModels;

namespace Assistant.UI.Windowing;

/// <summary>
/// Opens conversations in the History window (PROJECT_SPEC §4.2, §4.3). "Open in History window" on the floating
/// conversation moves it there: the History window comes forward with the conversation open in its workspace and its
/// card first in the list, and the floating panel animates away. The History window is created the first time it is
/// needed.
/// </summary>
internal sealed class HistoryWindowController
{
    private readonly ConversationViewModel _conversation;
    private readonly HistoryViewModel _history;
    private readonly IAssistantWindow _assistant;
    private readonly Lazy<IHistoryWindow> _historyWindow;

    public HistoryWindowController(
        ConversationViewModel conversation, HistoryViewModel history, IAssistantWindow assistant,
        Func<IHistoryWindow> historyWindow)
    {
        _conversation = conversation;
        _history = history;
        _assistant = assistant;
        _historyWindow = new Lazy<IHistoryWindow>(historyWindow);
        conversation.OpenInHistoryRequested += (_, _) => OpenInHistory();
    }

    /// <summary>
    /// Raised each time the user opens the full window (from Start, the icon or the bar): the bootstrapper looks for an update then, when one is due.
    /// </summary>
    public event EventHandler? Opened;

    /// <summary>Opens the History window as it is, for the bar's "Show history" action, and takes the Assistant's window away.</summary>
    public void ShowHistory()
    {
        _historyWindow.Value.ShowAndActivate();
        _assistant.Dismiss();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves the floating conversation into the History window.</summary>
    public void OpenInHistory()
    {
        if (_conversation.Messages.Count == 0)
        {
            return;
        }

        _history.Open(_conversation.Id, _conversation.Messages, _conversation.UpdatedAt, _conversation.Captures);

        // The History window opens where the panel is, while the panel still marks where the user is working.
        _historyWindow.Value.ShowAndActivate();
        _assistant.Dismiss();
    }
}
