using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Storage;
using Assistant.Tools.Integrations;
using Assistant.UI.Bootstrap;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The function test of step 110, with the real pieces and the real sample integration ("Sample Notes", a made-up program that touches nothing): a request for an app that
/// has no integration is answered with an offer, the user's click on Install installs it, and the Assistant carries on with the request by itself, with no word from the user;
/// asked again, the request goes straight to the installed integration. Nothing is downloaded from the web: the sample is served from this PC. Only the model is scripted,
/// so that it calls the tool it is offered and the test sees what it was given. It runs on the user interface's thread, as a conversation does.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private const string StepRequest = "List my notes in the Sample Notes app";

    // A model that calls the tool that lists notes when it is offered one, and says what the tool returned.
    private sealed class StepNoteModel : IModelService
    {
        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            if (request.Messages[^1].Role == MessageRole.Tool)
            {
                using var output = JsonDocument.Parse(request.Messages[^1].Text);
                yield return AssistantResponseChunk.ForTextDelta("Your notes: " + output.RootElement.GetProperty("content")[0].GetProperty("text").GetString());
            }
            else if (request.Tools.FirstOrDefault(tool => tool.Name == "mcp_samplenotes_list_notes") is { } tool)
            {
                yield return AssistantResponseChunk.ForToolCall(new ToolCall("call-1", tool.Name, "{}"));
            }
            else
            {
                yield return AssistantResponseChunk.ForTextDelta("I have no tool for that.");
            }
        }
    }

    private sealed class StepRecorder : IConversationRecorder
    {
        public List<(Guid Conversation, MessageViewModel Message)> Recorded { get; } = [];

        public void Record(Guid conversationId, MessageViewModel message) => Recorded.Add((conversationId, message));
    }

    // Runs the test on the user interface's thread, in a folder of its own that is deleted afterwards.
    private static async Task StepTestAsync(Func<string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-integration-resume-" + Guid.NewGuid().ToString("N"));
        try
        {
            var running = await UiDispatcher.Value.InvokeAsync(() => body(root));
            await running;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, recursive: true);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A program that is still ending holds a file; the folder is in the temp directory and goes in time.
            }
        }
    }

    private static IHost StepHost(string root, IModelService model)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Assistant",
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = "Development",
        });
        builder.Services.AddAssistantServices().AddUserInterface();
        builder.Services.AddSingleton(new AppPaths(root));
        builder.Services.AddSingleton(model);
        return builder.Build();
    }

    private static async Task StepWaitAsync(Func<bool> condition, string failure)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, failure);
            await Task.Delay(25);
        }
    }

    // Asks the request the way the conversation does, through the coordinator that also goes back to a request that was set aside.
    private static Task StepAskAsync(AnswerCoordinator coordinator, Guid conversation, string text, List<MessageViewModel> shown) =>
        coordinator.AskAsync(conversation, new MessageViewModel(MessageRole.User, text), shown.Add, () => true);

    [Fact]
    public Task ARequestForTheSampleIsOfferedThenInstalledThenCarriedOutByItselfAndAskedAgainGoesStraightToIt() => StepTestAsync(async root =>
    {
        var model = new StepNoteModel();
        using var host = StepHost(root, model);
        var recorder = new StepRecorder();
        var coordinator = new AnswerCoordinator(host.Services.GetRequiredService<IAnswerProvider>(), recorder);
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();
        var demo = host.Services.GetRequiredService<ISampleIntegrationDemo>();
        var registry = host.Services.GetRequiredService<IInstalledIntegrationRegistry>();

        // The request the demo suggests is not read as a search for files, so it reaches the Assistant's handling of apps.
        var files = host.Services.GetRequiredService<IFileRequestService>();
        Assert.False(files.IsFileRequest(StepRequest));
        Assert.False(files.IsLikelyFileRequest(StepRequest));

        // The demo is turned on by its own command, asked as any question is, and nothing is installed or run.
        await StepAskAsync(coordinator, conversation, "demo integration request", shown);
        var started = Assert.Single(shown);
        Assert.Contains("List my notes in the Sample Notes app", started.Text, StringComparison.Ordinal);
        Assert.Empty(await registry.ListAsync());
        Assert.Empty(model.Requests);
        shown.Clear();

        // 1. The request is answered with an offer: the model is not asked, and nothing is installed until the user clicks.
        await StepAskAsync(coordinator, conversation, StepRequest, shown);
        var offerMessage = Assert.Single(shown);
        Assert.Empty(model.Requests);
        Assert.Contains("no Sample Notes integration is installed", offerMessage.Text, StringComparison.Ordinal);
        var panel = Assert.IsType<IntegrationOfferContent>(offerMessage.Content.Last());
        Assert.Equal(IntegrationOfferState.Waiting, panel.State);
        Assert.Empty(await registry.ListAsync());
        Assert.False(coordinator.IsAnswering);

        // 2. The user clicks Install. The integration is installed, and the Assistant goes back to the request without being asked again.
        await panel.InstallAsync();
        Assert.Equal(IntegrationOfferState.Installed, panel.State);
        await StepWaitAsync(() => shown.Count == 2 && !coordinator.IsAnswering, "The request was not carried on with.");
        var carriedOut = shown[1];
        Assert.Equal(MessageRole.Assistant, carriedOut.Role);
        Assert.Contains("Your notes:", carriedOut.Text, StringComparison.Ordinal);
        Assert.Contains("Buy milk (sample)", carriedOut.Text, StringComparison.Ordinal);
        Assert.Equal(MessageStatus.Complete, carriedOut.Status);

        // The model was asked once to call the tool and once to say what it returned, for the request that was set aside, and the offer's words were not in what it was told.
        Assert.Equal(2, model.Requests.Count);
        Assert.Contains(model.Requests[0].Tools, tool => tool.Name == "mcp_samplenotes_list_notes");
        Assert.InRange(model.Requests[0].Tools.Count(tool => tool.Name.StartsWith("mcp_", StringComparison.Ordinal)), 1, 5);
        Assert.DoesNotContain(model.Requests[0].Messages, message => message.Text.Contains("passed my checks", StringComparison.Ordinal) || message.Text.Contains("no Sample Notes integration", StringComparison.Ordinal));
        var asked = Assert.Single(model.Requests[0].Messages);
        Assert.Equal((MessageRole.User, StepRequest), (asked.Role, asked.Text));

        // Every message was saved, in order, and no message of the user's was added for the request that was carried on with: they asked for the demo and made the request, twice
        // with the next question below, and that is all.
        Assert.Equal(
            [started.Id, offerMessage.Id, carriedOut.Id],
            recorder.Recorded.Where(item => item.Message.Role == MessageRole.Assistant).Select(item => item.Message.Id).Distinct().Take(3).ToArray());
        Assert.Equal(2, recorder.Recorded.Where(item => item.Message.Role == MessageRole.User).Select(item => item.Message.Id).Distinct().Count());

        // 3. The same request again goes straight to the installed integration: no offer, no search, no second installation.
        var before = model.Requests.Count;
        await StepAskAsync(coordinator, conversation, StepRequest, shown);
        var again = shown[^1];
        Assert.Equal(3, shown.Count);
        Assert.Contains("Your notes:", again.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(again.Content, part => part is IntegrationOfferContent);
        Assert.Equal(before + 2, model.Requests.Count);
        Assert.Single(await registry.ListAsync());
        Assert.Equal(["samplenotes"], Directory.GetDirectories(new AppPaths(root).IntegrationsDirectory).Select(path => Path.GetFileName(path)!).ToArray());
    });

    [Fact]
    public Task CancellingTheOfferInstallsNothingAndTheAssistantSaysSoAndTheRequestIsNotCarriedOut() => StepTestAsync(async root =>
    {
        var model = new StepNoteModel();
        using var host = StepHost(root, model);
        var coordinator = new AnswerCoordinator(host.Services.GetRequiredService<IAnswerProvider>());
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();
        await host.Services.GetRequiredService<ISampleIntegrationDemo>().StartRequestDemoAsync(CancellationToken.None);
        await StepAskAsync(coordinator, conversation, StepRequest, shown);
        var panel = Assert.IsType<IntegrationOfferContent>(Assert.Single(shown).Content.Last());

        panel.CancelCommand.Execute(null);

        await StepWaitAsync(() => shown.Count == 2 && !coordinator.IsAnswering, "The Assistant did not say that nothing was installed.");
        Assert.Equal(IntegrationOfferState.Cancelled, panel.State);
        Assert.Equal(
            "I didn't install the Sample Notes integration, so I didn't read your notes in Sample Notes. Ask me again whenever you want to set it up.", shown[1].Text);
        Assert.Empty(model.Requests);
        Assert.Empty(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());

        // The request is not carried out later by itself: the user's answer was no.
        await Task.Delay(200);
        Assert.Equal(2, shown.Count);
        Assert.Empty(model.Requests);
    });

    [Fact]
    public Task WithoutTheDemoTheSameRequestIsTheAssistantsOwnAnswerAndNothingIsOffered() => StepTestAsync(async root =>
    {
        var model = new StepNoteModel();
        using var host = StepHost(root, model);
        var coordinator = new AnswerCoordinator(host.Services.GetRequiredService<IAnswerProvider>());
        var shown = new List<MessageViewModel>();

        await StepAskAsync(coordinator, Guid.NewGuid(), StepRequest, shown);

        // Local Only mode is on, so the Assistant says what it would need to look; the made-up sample is not offered unless the demo is on.
        var message = Assert.Single(shown);
        Assert.DoesNotContain(message.Content, part => part is IntegrationOfferContent);
        Assert.Contains("Local Only", message.Text, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
    });
}
