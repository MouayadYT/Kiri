using Assistant.Core.Events;
using Assistant.Core.QuickSearch.Actions;
using Assistant.ModelHost.FakeEngine;
using Assistant.SmokeTests.Support;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Assistant.SmokeTests;

/// <summary>
/// Checklist 12: nothing that changes something is done before the user says yes. The model (a stand-in engine behind the app's real model host parts) asks for
/// the volume to be set; the app's agent loop, tool executor and confirmation put the question in the conversation with exactly what would be done, and the
/// change happens only after a yes. The one thing replaced is what would touch the PC (the system actions), by a recorder, so the check changes no setting.
/// </summary>
public sealed class ToolConfirmationSmokeTests
{
    private static readonly FakeEngineScenario SetsTheVolume = new()
    {
        Chat = new FakeChatReply
        {
            FinishReason = "tool_calls",
            Pieces = [],
            ToolCalls = [new FakeToolCall("set_volume", """{"percent":10}""")],
        },
    };

    [Fact]
    public Task TheModelAsksForAChange_TheUserIsAskedFirst_AndANoChangesNothing() => RunAsync(async (app, volume) =>
    {
        var panel = await AskAsync(app);

        Assert.Equal("Set the volume to 10%?", panel.Title);
        Assert.Equal(ToolConfirmationState.Pending, panel.State);
        Assert.Empty(volume.Calls);

        // A yes pressed at once does nothing (a click meant for something else); only the button that says no works.
        panel.ApproveCommand.Execute(null);
        Assert.Equal(ToolConfirmationState.Pending, panel.State);
        panel.DeclineCommand.Execute(null);

        await Wait.UntilAsync(() => panel.State == ToolConfirmationState.Declined, "The no was not taken.");
        await Wait.UntilAsync(() => !app.Get<ConversationViewModel>().IsAnswering, "The answer did not end after the no.");
        Assert.Equal("Not allowed. Nothing was done.", panel.ResultText);
        Assert.Empty(volume.Calls);
    });

    [Fact]
    public Task AYesRunsExactlyWhatWasAskedAbout_OnceTheButtonIsArmed() => RunAsync(async (app, volume) =>
    {
        var panel = await AskAsync(app);
        Assert.Empty(volume.Calls);

        await Wait.UntilAsync(() => panel.IsArmed, "The button that says yes never became ready.");
        panel.ApproveCommand.Execute(null);

        await Wait.UntilAsync(() => volume.Calls.Count > 0, "The change was not made after the yes.");
        Assert.Equal(ToolConfirmationState.Approved, panel.State);
        Assert.Equal([10], volume.Calls);
        await Wait.UntilAsync(() => !app.Get<ConversationViewModel>().IsAnswering, "The answer did not end after the yes.");
        Assert.Equal([10], volume.Calls);
    });

    // Asks the question and waits for the Assistant's question to appear in the answer.
    private static async Task<ToolConfirmationContent> AskAsync(SmokeApp app)
    {
        var conversation = app.Get<ConversationViewModel>();
        conversation.StartNew("Set the volume to 10 percent");
        ToolConfirmationContent? panel = null;
        await Wait.UntilAsync(
            () => (panel = conversation.Messages.Skip(1).SelectMany(message => message.Content).OfType<ToolConfirmationContent>().FirstOrDefault()) is not null,
            () => "The Assistant did not ask first: " + string.Join(" | ", conversation.Messages.Select(message => message.Status + ": " + message.Text))
                + " || " + string.Join(" // ", app.Logs.Lines.Where(line => !line.StartsWith("Debug", StringComparison.Ordinal)).TakeLast(6)));
        return panel!;
    }

    private static Task RunAsync(Func<SmokeApp, RecordingVolume, Task> body) => Smoke.RunInFolderAsync(async scratch =>
    {
        var volume = new RecordingVolume();
        await using var model = FakeLocalModel.Create(scratch, SetsTheVolume);
        await using var app = await SmokeApp.StartAsync(scratch, services =>
        {
            model.Replace(services);
            services.AddSingleton<ISystemActions>(volume);
        });
        await app.ChangeSettingsAsync(model.Use);
        await body(app, volume);
    });

    /// <summary>What would change the volume, writing down instead of changing it.</summary>
    private sealed class RecordingVolume : ISystemActions
    {
        public List<int> Calls { get; } = [];

        public bool SetVolume(int percent)
        {
            Calls.Add(percent);
            return true;
        }

        public bool OpenWindowsSettings() => false;

        public bool LockWorkstation() => false;

        public bool SetMuted(bool muted) => false;

        public int? ChangeVolume(int percent) => null;

        public VolumeState? GetVolume() => new(50, false);

        public string? GetFolder(SystemFolder folder) => null;
    }
}
