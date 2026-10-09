using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Events;
using Assistant.Core.People;
using Assistant.Core.Storage;
using Assistant.Tools.Integrations;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Assistant.UI.Tests;

/// <summary>
/// The function test of step 116, with the real pieces and the real made-up apps (a calendar and a messaging app that ship beside the app, run as the programs they are): "demo reminder" turns
/// the demo on, the request "Check my calendar for exams in the next two weeks and message my brother to remind him" is answered with an offer of the calendar, then of the messaging app,
/// each installed only by the user's click and each followed by the request being carried on with by itself, and then the model, in the real loop, reads the calendar, picks out the
/// exams, drafts the reminder for the made-up brother and asks the user inline; the message is sent only when the user allows it. Only the model is scripted. It runs on the user
/// interface's thread, as a conversation does.
/// </summary>
public sealed partial class PromptInputControlTests
{
    private const string ReminderRequest = "Check my calendar for exams in the next two weeks and message my brother to remind him.";
    private const string ReminderText = "Reminder: you have a Physics final exam, a Calculus midterm and a Biology quiz in the next two weeks.";

    // A model that does the job in order when it is offered the tools for it: reads the calendar, drafts the reminder, sends it, and says what the last result said.
    private sealed class ReminderModel : IModelService
    {
        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("test-model", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.CompletedTask;
            var results = request.Messages.Where(message => message.Role == MessageRole.Tool).ToList();
            var today = DateTime.Now.Date;
            string Day(int days) => today.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string Args(object value) => JsonSerializer.Serialize(value);
            if (results.Count == 0 && request.Tools.Any(tool => tool.Name == "mcp_samplecalendar_list_events"))
            {
                yield return AssistantResponseChunk.ForToolCall(new ToolCall("c1", "mcp_samplecalendar_list_events", Args(new { start = Day(0), end = Day(14) })));
            }
            else if (results.Count == 1 && request.Tools.Any(tool => tool.Name == "draft_message"))
            {
                yield return AssistantResponseChunk.ForToolCall(new ToolCall("c2", "draft_message", Args(new { recipient = "my brother", text = ReminderText })));
            }
            else if (results.Count == 2 && request.Tools.Any(tool => tool.Name == "send_message"))
            {
                yield return AssistantResponseChunk.ForToolCall(new ToolCall("c3", "send_message", Args(new { recipient = "my brother", text = ReminderText })));
            }
            else if (results.Count == 3)
            {
                using var last = JsonDocument.Parse(results[2].Text);
                yield return AssistantResponseChunk.ForTextDelta(last.RootElement.TryGetProperty("status", out var status) && status.GetString() == "sent"
                    ? "I found the exams and sent the reminder to Omar (a sample: no one got it)."
                    : "I did not send the reminder.");
            }
            else
            {
                yield return AssistantResponseChunk.ForTextDelta("I have no tools for that.");
            }
        }
    }

    private static async Task ReminderTestAsync(Func<string, Task> body)
    {
        var root = Path.Combine(Path.GetTempPath(), "assistant-reminder-" + Guid.NewGuid().ToString("N"));
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

    private static Task ReminderAskAsync(AnswerCoordinator coordinator, Guid conversation, string text, List<MessageViewModel> shown) =>
        coordinator.AskAsync(conversation, new MessageViewModel(MessageRole.User, text), shown.Add, () => true);

    private static async Task ReminderWaitAsync(Func<bool> condition, string failure)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < until, failure);
            await Task.Delay(25);
        }
    }

    // The two installs, each by the user's click, each followed by the request being carried on with by itself; ends when the model is running the job.
    private static async Task<(AnswerCoordinator Coordinator, List<MessageViewModel> Shown, Guid Conversation)> ReminderInstalledAsync(IHost host, ReminderModel model)
    {
        var coordinator = new AnswerCoordinator(host.Services.GetRequiredService<IAnswerProvider>());
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();
        await ReminderAskAsync(coordinator, conversation, "demo reminder", shown);
        await ReminderAskAsync(coordinator, conversation, ReminderRequest, shown);
        var calendarPanel = Assert.IsType<IntegrationOfferContent>(shown[^1].Content.Last());
        await calendarPanel.InstallAsync();
        await ReminderWaitAsync(() => shown.Count == 3 && !coordinator.IsAnswering, "The request was not carried on with after the calendar.");
        var messagesPanel = Assert.IsType<IntegrationOfferContent>(shown[^1].Content.Last());
        Assert.Empty(model.Requests);
        await messagesPanel.InstallAsync();
        return (coordinator, shown, conversation);
    }

    [Fact]
    public Task TheCalendarAndMessagingAppAreOfferedInstalledOnClickTheJobIsDoneAskedAndSentOnlyOnAYes() => ReminderTestAsync(async root =>
    {
        var model = new ReminderModel();
        using var host = StepHost(root, model);
        var registry = host.Services.GetRequiredService<IInstalledIntegrationRegistry>();
        var coordinator = new AnswerCoordinator(host.Services.GetRequiredService<IAnswerProvider>());
        var conversation = Guid.NewGuid();
        var shown = new List<MessageViewModel>();

        // Turning the demo on installs nothing, asks nothing and says what to ask.
        await ReminderAskAsync(coordinator, conversation, "demo reminder", shown);
        Assert.Contains("Check my calendar for exams in the next two weeks and message my brother to remind him.", Assert.Single(shown).Text, StringComparison.Ordinal);
        Assert.Empty(await registry.ListAsync());
        Assert.Empty(model.Requests);

        // 1. The request is answered with the offer of the calendar: the model is not asked and nothing is installed until the click.
        await ReminderAskAsync(coordinator, conversation, ReminderRequest, shown);
        var calendarOffer = shown[^1];
        Assert.Contains("I can't read your calendar yet: no calendar integration is installed.", calendarOffer.Text, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
        var calendarPanel = Assert.IsType<IntegrationOfferContent>(calendarOffer.Content.Last());
        Assert.Empty(await registry.ListAsync());

        // 2. The click installs it and the request goes on by itself, to the next thing nothing serves: the messaging app, offered the same way.
        await calendarPanel.InstallAsync();
        Assert.Equal(IntegrationOfferState.Installed, calendarPanel.State);
        await ReminderWaitAsync(() => shown.Count == 3 && !coordinator.IsAnswering, "The request was not carried on with after the calendar.");
        var messagesOffer = shown[^1];
        Assert.Contains("I can't send a message yet: no messaging integration is installed.", messagesOffer.Text, StringComparison.Ordinal);
        Assert.Empty(model.Requests);
        var messagesPanel = Assert.IsType<IntegrationOfferContent>(messagesOffer.Content.Last());
        Assert.Equal(["samplecalendar"], (await registry.ListAsync()).Select(integration => integration.Id).ToArray());

        // 3. The second click installs it, and now the request is the model's.
        await messagesPanel.InstallAsync();
        await ReminderWaitAsync(() => shown.Count == 4 && shown[^1].Content.OfType<ToolConfirmationContent>().Any(), "The question was not asked.");
        var installed = await registry.ListAsync();
        Assert.Equal(["samplecalendar", "samplemessages"], installed.Select(integration => integration.Id).ToArray());
        Assert.All(installed, integration => Assert.True(integration.IsSample));
        Assert.Contains("list_events", installed[0].Permissions.ReadOnlyTools);
        Assert.DoesNotContain("send_message", installed[1].Permissions.ReadOnlyTools);

        // The model was given the calendar's own tool and the Assistant's messaging tools, and not the messaging app's own, and was told the order to do the job in.
        var offered = model.Requests[0].Tools.Select(tool => tool.Name).ToList();
        Assert.Contains("mcp_samplecalendar_list_events", offered);
        Assert.Contains("draft_message", offered);
        Assert.Contains("send_message", offered);
        Assert.DoesNotContain(offered, name => name.StartsWith("mcp_samplemessages", StringComparison.Ordinal));
        Assert.Contains("check their calendar and then message someone", model.Requests[0].Instructions, StringComparison.Ordinal);
        var asked = Assert.Single(model.Requests[0].Messages);
        Assert.Equal((MessageRole.User, ReminderRequest), (asked.Role, asked.Text));

        // The question, in the answer: who, where, and the whole text. The message is not sent until the user allows it.
        var answer = shown[^1];
        var question = Assert.Single(answer.Content.OfType<ToolConfirmationContent>());
        Assert.Equal(ConfirmationKind.SendMessage, question.Confirmation.Kind);
        Assert.Contains(question.Details, line => line.Label == "To" && line.Value == "Omar");
        Assert.Contains(question.Details, line => line.Label == "Through" && line.Value.Contains("Sample Messages", StringComparison.Ordinal) && line.Value.Contains("a sample", StringComparison.Ordinal));
        Assert.Contains(question.Details, line => line.Label == "Message" && line.Value == ReminderText);
        Assert.Equal(ToolConfirmationState.Pending, question.State);
        Assert.Equal(3, model.Requests.Count);

        await ReminderWaitAsync(() => question.CanApprove, "The button that says yes never armed.");
        question.Approve();
        await ReminderWaitAsync(() => answer.Status == MessageStatus.Complete && !coordinator.IsAnswering, "The answer did not end.");

        Assert.Equal(ToolConfirmationState.Approved, question.State);
        Assert.Equal("I found the exams and sent the reminder to Omar (a sample: no one got it).", answer.Text);
        Assert.Equal(4, model.Requests.Count);

        // What the model was told at each step: the exams the Assistant found, who the message was for, and that it was sent to a sample.
        var tools = model.Requests[3].Messages.Where(message => message.Role == MessageRole.Tool).Select(message => JsonDocument.Parse(message.Text).RootElement).ToList();
        var check = tools[0].GetProperty("exam_check");
        Assert.Equal(3, check.GetProperty("likely").GetArrayLength());
        Assert.Equal("Omar", tools[1].GetProperty("to").GetString());
        Assert.Equal("sent", tools[2].GetProperty("status").GetString());
        Assert.True(tools[2].GetProperty("sample").GetBoolean());

        // The user's own list of people was never read or written: Omar lives in the demo's own list.
        Assert.Empty(await host.Services.GetRequiredService<IPersonStore>().ListAsync());
    });

    [Fact]
    public Task WhenTheUserDoesNotAllowItNothingIsSentAndTheAssistantSaysSo() => ReminderTestAsync(async root =>
    {
        var model = new ReminderModel();
        using var host = StepHost(root, model);
        var (coordinator, shown, _) = await ReminderInstalledAsync(host, model);
        await ReminderWaitAsync(() => shown.Count == 4 && shown[^1].Content.OfType<ToolConfirmationContent>().Any(), "The question was not asked.");
        var answer = shown[^1];
        var question = Assert.Single(answer.Content.OfType<ToolConfirmationContent>());

        question.Decline();
        await ReminderWaitAsync(() => answer.Status == MessageStatus.Complete && !coordinator.IsAnswering, "The answer did not end.");

        Assert.Equal(ToolConfirmationState.Declined, question.State);
        Assert.Equal("I did not send the reminder.", answer.Text);
        Assert.True(Assistant.Core.Tools.ToolErrors.TryRead(model.Requests[3].Messages.Last(message => message.Role == MessageRole.Tool).Text, out var code, out _));
        Assert.Equal(Assistant.Core.Tools.ToolErrors.Declined, code);
    });

    [Fact]
    public Task CancellingTheOfferOfTheCalendarInstallsNothingAndTheRequestIsNotCarriedOut() => ReminderTestAsync(async root =>
    {
        var model = new ReminderModel();
        using var host = StepHost(root, model);
        var coordinator = new AnswerCoordinator(host.Services.GetRequiredService<IAnswerProvider>());
        var shown = new List<MessageViewModel>();
        await ReminderAskAsync(coordinator, Guid.NewGuid(), "demo reminder", shown);
        await ReminderAskAsync(coordinator, Guid.NewGuid(), ReminderRequest, shown);
        var panel = Assert.IsType<IntegrationOfferContent>(shown[^1].Content.Last());

        panel.CancelCommand.Execute(null);

        await ReminderWaitAsync(() => shown.Count == 3 && !coordinator.IsAnswering, "The Assistant did not say that nothing was installed.");
        Assert.Equal("I didn't install Sample Calendar, so I didn't read your calendar. Ask me again whenever you want to set it up.", shown[^1].Text);
        Assert.Empty(model.Requests);
        Assert.Empty(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
    });

    [Fact]
    public Task WithoutTheDemoTheRequestIsTheModelsAndNothingIsOfferedOrSentAndNoMessagingToolIsOffered() => ReminderTestAsync(async root =>
    {
        var model = new ReminderModel();
        using var host = StepHost(root, model);
        var coordinator = new AnswerCoordinator(host.Services.GetRequiredService<IAnswerProvider>());
        var shown = new List<MessageViewModel>();

        await ReminderAskAsync(coordinator, Guid.NewGuid(), ReminderRequest, shown);

        // Nothing is offered, the model is asked as it always was, and it has no calendar tool and no messaging tool: Messaging is not available in this version.
        var answer = Assert.Single(shown);
        Assert.DoesNotContain(answer.Content, part => part is IntegrationOfferContent);
        var request = Assert.Single(model.Requests);
        Assert.DoesNotContain(request.Tools, tool => tool.Name is "draft_message" or "send_message" or "get_calendar_events" or "search_calendar_events");
        Assert.Empty(await host.Services.GetRequiredService<IInstalledIntegrationRegistry>().ListAsync());
    });

    [Fact]
    public Task TurningTheDemoOffTakesTheMadeUpBrotherAndThePermissionsAway() => ReminderTestAsync(async root =>
    {
        var model = new ReminderModel();
        using var host = StepHost(root, model);
        var (coordinator, shown, conversation) = await ReminderInstalledAsync(host, model);
        await ReminderWaitAsync(() => shown.Count == 4 && shown[^1].Content.OfType<ToolConfirmationContent>().Any(), "The question was not asked.");
        shown[^1].Content.OfType<ToolConfirmationContent>().Single().Decline();
        await ReminderWaitAsync(() => !coordinator.IsAnswering, "The answer did not end.");

        await ReminderAskAsync(coordinator, conversation, "demo reminder off", shown);
        var off = shown[^1];
        Assert.Contains("The reminder demo is off", off.Text, StringComparison.Ordinal);
        var permissions = host.Services.GetRequiredService<IPermissionPolicy>();
        var resolver = host.Services.GetRequiredService<IPersonResolver>();
        var before = model.Requests.Count;
        await ReminderAskAsync(coordinator, conversation, ReminderRequest, shown);

        // Messaging is again what the user's Settings say (not in this version), the brother is the user's own list (no one), and the installed samples are used only as the apps they are.
        Assert.False((await permissions.CheckAsync(PermissionCapability.Messaging)).IsAllowed);
        Assert.Equal(PersonResolutionOutcome.NotFound, (await resolver.ResolveAsync("my brother")).Outcome);
        Assert.DoesNotContain(model.Requests[before].Tools, tool => tool.Name is "draft_message" or "send_message");
    });
}
