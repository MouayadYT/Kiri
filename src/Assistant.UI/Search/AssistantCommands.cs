using Assistant.UI.Capture;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Microsoft.Extensions.DependencyInjection;

namespace Assistant.UI.Search;

/// <summary>
/// What the bar's own quick actions do to the Assistant (PROJECT_SPEC §4.1): they reach the window's controllers when they are run, not
/// when they are wired, since the controllers need the bar's results, which need these actions. Each runs on the UI thread.
/// </summary>
internal sealed class AssistantCommands(IServiceProvider services) : IAssistantCommands
{
    /// <inheritdoc/>
    public void NewConversation()
    {
        // Every question asked from the bar starts a conversation of its own, so a new one only needs an empty bar, ready for the question.
        services.GetRequiredService<SearchOrAskViewModel>().Query = "";
        services.GetRequiredService<SearchResultsViewModel>().ResetScope();
    }

    /// <inheritdoc/>
    public void ShowHistory() => services.GetRequiredService<HistoryWindowController>().ShowHistory();

    /// <inheritdoc/>
    public void OpenSettings() => services.GetRequiredService<ISettingsLauncher>().Show();

    /// <inheritdoc/>
    public void TakeScreenshot() => _ = services.GetRequiredService<VisualIntelligenceController>().InvokeAsync();
}
