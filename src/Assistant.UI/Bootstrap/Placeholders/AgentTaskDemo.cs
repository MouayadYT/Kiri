using System.Runtime.CompilerServices;
using Assistant.Core.Audit;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Orchestration;
using Assistant.Core.Tools;
using Assistant.Tools;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.UI.Bootstrap.Placeholders;

/// <summary>The developer's test of a task with several steps (<c>demo task</c>, <c>demo task fail</c>).</summary>
internal interface IAgentTaskDemo
{
    /// <summary>
    /// Runs the demonstration into the conversation <paramref name="conversationId"/> (a conversation of its own when <see langword="null"/>): a made-up task with several
    /// steps, whose panel is shown in the answer, with a slow step that the user can stop, a step that asks first, and, when <paramref name="failing"/>, a step that does not work.
    /// </summary>
    Task RunAsync(Guid? conversationId, bool failing, Action<MessageViewModel> show, CancellationToken cancellationToken);
}

/// <summary>
/// <c>demo task</c> and <c>demo task fail</c> (PROJECT_SPEC §4.8, step 117): shows what it is like when the Assistant carries out a request in several steps. The real thing is used
/// from end to end: the real agent loop runs a made-up model's plan (search for a file, read it slowly, open an application) over made-up tools that touch nothing, the real executor asks
/// the real question before the step that changes something, the real answer shows the real panel (each step as it goes, the button that stops the task, where it could not go on) and the
/// real activity log keeps what happened, so the steps are in Settings > Activity afterwards. In <c>demo task fail</c> the file cannot be read, so the task ends at that step.
/// </summary>
internal sealed class AgentTaskDemo(
    IAppEventBus bus, IAgentTaskLog tasks, IPermissionService confirmations, ISettingsService settings, IImagePreprocessor images, TimeProvider clock,
    ISettingsLauncher? launcher = null, TimeSpan? slowStep = null) : IAgentTaskDemo
{
    /// <summary>How long the slow step takes, so that there is time to press Cancel while it works.</summary>
    internal static readonly TimeSpan SlowStep = TimeSpan.FromSeconds(8);

    private const string Introduction =
        "Here is what a task with several steps looks like. Everything in it is made up: no file is searched or read and no application is opened, and nothing leaves this PC. "
        + "I will search for a file, read it (that step is slow, so you have time to press Cancel in the panel below and see the task stop), and ask you before I open an application. "
        + "Afterwards the steps are listed in Settings > Activity.";

    private const string DoneText = "That is all three steps done. Nothing on this PC was touched. Open Settings > Activity to see them listed, with what you answered.";

    private const string FailedText =
        "I could not read that file, so I stopped there and did not open the application. The panel above says where the task could not go on.";

    private static readonly ToolDefinition Search = ToolDefinition.Create(
        "search_files", "Finds a file (a made-up one, for a test).", [new ToolParameter("query", ToolParameterType.String, "What to look for.")], RiskLevel.ReadOnly);

    private static readonly ToolDefinition Read = ToolDefinition.Create(
        "read_file_text", "Reads a file (a made-up one, slowly, for a test).", [new ToolParameter("name", ToolParameterType.String, "The file.")], RiskLevel.ReadOnly,
        timeout: TimeSpan.FromSeconds(30));

    private static readonly ToolDefinition Open = ToolDefinition.Create(
        "open_application", "Opens an application (it opens nothing, for a test).", [new ToolParameter("application", ToolParameterType.String, "The application.")],
        RiskLevel.SideEffect);

    /// <inheritdoc/>
    public async Task RunAsync(Guid? conversationId, bool failing, Action<MessageViewModel> show, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(show);
        var tools = new ITool[]
        {
            new HandlerTool(Search, (call, _, _, _) => Task.FromResult(Succeeded(call, """{"found":1}"""))),
            new HandlerTool(Read, (call, _, _, token) => ReadAsync(call, failing, slowStep ?? SlowStep, token)),
            new HandlerTool(
                Open,
                (call, _, _, _) => Task.FromResult(Succeeded(call, """{"opened":false,"note":"a test: nothing was opened"}""")),
                confirmation: _ => new ToolConfirmation(
                    ConfirmationKind.Launch,
                    "Open Notepad?",
                    [new ConfirmationDetail("Application", "Notepad"), new ConfirmationDetail("Note", "This is a test: nothing will be opened.")],
                    "Open")),
        };

        var orchestrator = new AssistantOrchestrator(
            new PlanModel(failing), settings, new PromptBuilder(), images, clock, NullLogger<AssistantOrchestrator>.Instance,
            toolRegistry: new ToolRegistry(tools), toolExecutor: new ToolExecutor(tools, confirmations), taskLog: tasks);

        // The real answer provider streams the plan into the conversation, so the panel, the question and the Cancel button are the ones the Assistant really has.
        var provider = new ModelAnswerProvider(
            orchestrator, clock, bus: bus, settings: launcher, tasks: tasks);
        await provider.StreamAnswerAsync(conversationId ?? Guid.NewGuid(), failing ? "demo task fail" : "demo task", show, cancellationToken).ConfigureAwait(true);
    }

    private static async Task<ToolResult> ReadAsync(ToolCall call, bool failing, TimeSpan slow, CancellationToken cancellationToken)
    {
        if (failing)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false);
            return ToolErrors.Result(call, ToolResultStatus.Failed, ToolErrors.Failed, "The file could not be read.");
        }

        await Task.Delay(slow, cancellationToken).ConfigureAwait(false);
        return Succeeded(call, """{"text":"a made-up page"}""");
    }

    private static ToolResult Succeeded(ToolCall call, string output) => new(call.Id, call.ToolName, ToolResultStatus.Succeeded, output);

    // The plan of the task, as a model would make it: it says what it will do, then calls one tool after another, whatever the conversation says.
    private sealed class PlanModel(bool failing) : IModelService
    {
        private int _round;

        public Task<ModelInfo?> GetActiveModelAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ModelInfo?>(new ModelInfo("task-demo", 8192) { SupportsToolCalling = true });

        public async IAsyncEnumerable<AssistantResponseChunk> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            switch (_round++)
            {
                case 0:
                    yield return AssistantResponseChunk.ForTextDelta(Introduction);
                    yield return AssistantResponseChunk.ForToolCall(new ToolCall("demo-1", "search_files", """{"query":"quarterly report"}"""));
                    break;
                case 1:
                    yield return AssistantResponseChunk.ForToolCall(new ToolCall("demo-2", "read_file_text", """{"name":"quarterly-report.txt"}"""));
                    break;
                case 2 when !failing:
                    yield return AssistantResponseChunk.ForToolCall(new ToolCall("demo-3", "open_application", """{"application":"Notepad"}"""));
                    break;
                default:
                    yield return AssistantResponseChunk.ForTextDelta(failing ? FailedText : DoneText);
                    break;
            }

            await Task.CompletedTask;
        }
    }
}
