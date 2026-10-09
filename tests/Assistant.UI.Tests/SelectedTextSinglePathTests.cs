using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Assistant.Core.Budgeting;
using Assistant.Core.Context;
using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.Ipc;
using Assistant.Core.Orchestration;
using Assistant.Core.Settings;
using Assistant.UI.Bootstrap.Placeholders;
using Assistant.UI.Browser;
using Assistant.UI.Selection;
using Assistant.UI.ViewModels;
using Assistant.UI.Windowing;
using Assistant.Windows.Imaging;
using Assistant.Windows.Selection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.UI.Tests;

public sealed partial class PromptInputControlTests
{
    // ---- Selected-text checkpoint (step 90): however a selection is acquired, one path takes it from there ----------------------------

    private const string CheckpointWords = "The offsite moves to the second week of March.";

    // Delegates to the real service and keeps what was added to it, so a test can see every way context arrives.
    private sealed class RecordingContexts(IContextService inner) : IContextService
    {
        public List<(ContextItemType Type, ContextSource Source, string Origin, bool HasText)> Added { get; } = [];

        public ContextAddResult Add(Guid conversationId, ContextItem item, string origin)
        {
            Added.Add((item.Type, item.Source, origin, !string.IsNullOrWhiteSpace(item.Text)));
            return inner.Add(conversationId, item, origin);
        }

        public bool Remove(Guid conversationId, Guid itemId) => inner.Remove(conversationId, itemId);

        public IReadOnlyList<ContextItem> PendingItems(Guid conversationId) => inner.PendingItems(conversationId);

        public void Commit(Guid conversationId, IReadOnlyList<ContextItem> items) => inner.Commit(conversationId, items);

        public ConversationContext GetContext(Guid conversationId) => inner.GetContext(conversationId);

        public BudgetedConversation Prepare(
            Guid conversationId, string instructions, IReadOnlyList<Message> conversation, ModelInfo model, ContextLimitSettings limits) =>
            inner.Prepare(conversationId, instructions, conversation, model, limits);

        public void Forget(Guid conversationId) => inner.Forget(conversationId);
    }

    private sealed class CheckpointRig
    {
        public required ChattyModel Model { get; init; }
        public required RecordingContexts Contexts { get; init; }
        public required ConversationViewModel Conversation { get; init; }
        public required AssistantWindowStateController Windows { get; init; }
        public required FakeShell Window { get; init; }
    }

    // A conversation over the real answer provider, orchestrator and context service, as the app has it.
    private static CheckpointRig CreateCheckpointRig()
    {
        var model = new ChattyModel();
        var contexts = new RecordingContexts(new ContextService(new ContextBudgeter(new HeuristicTokenEstimator())));
        var orchestrator = new AssistantOrchestrator(
            model, new InMemorySettingsService(), new PromptBuilder(contexts), new ImagePreprocessor(), new FixedClock(Now),
            NullLogger<AssistantOrchestrator>.Instance, contexts);
        var provider = new ModelAnswerProvider(orchestrator, new FixedClock(Now), null, null, contexts);
        var conversation = new ConversationViewModel(new VoiceInputViewModel(new FakeMicrophone()), provider);
        var window = new FakeShell();
        return new CheckpointRig
        {
            Model = model, Contexts = contexts, Conversation = conversation, Window = window,
            Windows = new AssistantWindowStateController(window, CreateBarModel(), conversation),
        };
    }

    private static void AskAboutSelection(CheckpointRig rig)
    {
        Assert.Single(rig.Conversation.Texts);
        rig.Conversation.Draft = "When is it?";
        rig.Conversation.AskCommand.Execute(null);
        WaitUntil(() => rig.Model.Requests.Count == 1 && !rig.Conversation.IsAnswering, "The question was not answered.");
    }

    public static TheoryData<string> SelectionRoutes => ["ui-automation", "browser-extension", "copy-fallback"];

    [Theory]
    [MemberData(nameof(SelectionRoutes))]
    public void EveryWayOfAcquiringASelection_ReachesTheModelThroughTheOneContextServicePath(string route) => RunSta(() =>
    {
        var rig = CreateCheckpointRig();
        var permissions = new PerCapabilityPermissions();
        permissions.Set(PermissionCapability.SelectedText, true);
        permissions.Set(PermissionCapability.SelectedTextByCopy, true);
        var app = new ForegroundApp(1234, "someapp", @"C:\Apps\someapp.exe", 0x1000);

        Task acquiring = route switch
        {
            "ui-automation" => new AskSelectionController(
                new FakeSelectionService { Result = SelectionResult.Selected(app, CheckpointWords, false) },
                permissions, rig.Windows, rig.Conversation, NullLogger<AskSelectionController>.Instance, AssistantProcess).InvokeAsync(),
            "browser-extension" => new AskBrowserSelectionController(
                permissions, rig.Windows, rig.Conversation, NullLogger<AskBrowserSelectionController>.Instance)
                .OpenAsync(new BrowserSelection(CheckpointWords, false, "A page", "https://example.test/p", "Microsoft Edge")),
            _ => new AskSelectionController(
                new FakeSelectionService { Result = SelectionResult.Unsupported(app) },
                permissions, rig.Windows, rig.Conversation, NullLogger<AskSelectionController>.Instance, AssistantProcess,
                new FakeCopyService { Result = CopySelectionResult.Copied(app, CheckpointWords, false, ClipboardRestoreOutcome.Restored) },
                new InMemorySettingsService()).InvokeByCopyAsync(),
        };
        WaitUntil(() => acquiring.IsCompleted, "The selection was not acquired.");

        // Acquiring only attaches it to the conversation (a chip): nothing has reached the context service or the model yet.
        Assert.Empty(rig.Contexts.Added);
        Assert.Empty(rig.Model.Requests);

        AskAboutSelection(rig);

        // One item, of one kind, from one origin, whichever way it was acquired; and the model got its words through the prompt builder.
        var added = Assert.Single(rig.Contexts.Added);
        Assert.Equal(ContextItemType.Selection, added.Type);
        Assert.Equal(ContextSource.UserSelected, added.Source);
        Assert.Equal("composer", added.Origin);
        Assert.True(added.HasText);
        var sent = rig.Model.Requests.Single().Messages[^1].Text;
        Assert.Contains("<untrusted_context id=\"1\" kind=\"selection\"", sent, StringComparison.Ordinal);
        Assert.Contains(CheckpointWords, sent, StringComparison.Ordinal);
        Assert.EndsWith("When is it?", sent, StringComparison.Ordinal);

        // Once asked, the context service holds the descriptor and not the words.
        var earlier = Assert.Single(rig.Contexts.GetContext(rig.Conversation.Id).Earlier).Item;
        Assert.Null(earlier.Text);
    });

    [Fact]
    public void OnlyTheAnswerProvider_TurnsASelectionIntoContext_AndNoAcquisitionCodeTouchesTheContextService()
    {
        var root = CheckpointRoot();
        var contextItems = new Regex(@"ContextItemType\.(Selection|Page)\b", RegexOptions.Compiled);
        var contextService = new Regex(@"\bIContextService\b|\bContextService\b", RegexOptions.Compiled);

        // Every place that builds a Selection or Page context item, anywhere in the source tree besides the prompt builder's own labels.
        var builders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains(@"\obj\", StringComparison.Ordinal) && !file.Contains(@"\bin\", StringComparison.Ordinal))
            .Where(file => contextItems.IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetFileName(file))
            .Order()
            .ToArray();
        Assert.Equal(["ModelAnswerProvider.cs", "PromptBuilder.cs"], builders);

        // The code that acquires a selection (UI Automation, the browser, the copy fallback) never talks to the context service: it only
        // produces a TextAttachment, which the answer provider hands over when a question is asked.
        var acquisition = new[]
        {
            Path.Combine(root, @"src\Assistant.UI\Selection"),
            Path.Combine(root, @"src\Assistant.UI\Browser"),
            Path.Combine(root, @"src\Assistant.Windows\Selection"),
            Path.Combine(root, @"src\Assistant.BrowserBridge"),
        };
        var offenders = new List<string>();
        var scanned = 0;
        foreach (var folder in acquisition)
        {
            Assert.True(Directory.Exists(folder), $"{folder} is not where the check looks for it.");
            foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories)
                         .Where(file => !file.Contains(@"\obj\", StringComparison.Ordinal) && !file.Contains(@"\bin\", StringComparison.Ordinal)))
            {
                scanned++;
                if (contextService.IsMatch(File.ReadAllText(file)))
                {
                    offenders.Add(Path.GetRelativePath(root, file));
                }
            }
        }

        Assert.True(scanned > 10, $"only {scanned} files were looked at");
        Assert.Empty(offenders);

        // And the one place that does add it says where it came from.
        var provider = File.ReadAllText(Path.Combine(root, @"src\Assistant.UI\Bootstrap\Placeholders\ModelAnswerProvider.cs"));
        Assert.Single(Regex.Matches(provider, @"_contexts\.Add\("));
    }

    // This file is two folders below the repository's root; the tests may run from a build folder elsewhere.
    private static string CheckpointRoot([CallerFilePath] string testFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(testFile)!, "..", ".."));
}
