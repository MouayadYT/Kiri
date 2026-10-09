using Assistant.Core.Contracts;
using Assistant.Core.Domain;
using Assistant.Core.ModelHosting;
using Assistant.Core.Settings;
using Assistant.ModelHost.FakeEngine;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.ModelHost.Tests;

/// <summary>
/// Generating from the app's side: the <see cref="LocalModelService"/> over the lifecycle, the client and a real
/// session, with the fake engine answering over its socket.
/// </summary>
public sealed partial class ModelLifecycleTests
{
    private static readonly ModelRequest Question = new(
        SampleMessages.PrivateInstructions,
        [new Message(Guid.NewGuid(), MessageRole.User, SampleMessages.PrivatePrompt, DateTimeOffset.UnixEpoch)]);

    [Fact]
    public async Task TheFirstQuestion_StartsTheHostAndLoadsTheModel_ThenStreamsTheAnswer()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out var statuses);
        var models = CreateService(lifecycle, setup.ScenarioPath);

        var chunks = await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));

        Assert.Equal(["Hello", " there", "!"], chunks.Select(chunk => chunk.Text));
        Assert.All(chunks, chunk => Assert.Equal(AssistantResponseChunkType.TextDelta, chunk.Type));
        Assert.Equal(1, host.Launcher.Started);
        Assert.Equal([ModelStatus.Loading, ModelStatus.Ready], statuses.Values.Select(status => status.Status));
        Assert.Equal("model", lifecycle.Model?.Id);

        // The next question uses the loaded model.
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        Assert.Equal(1, setup.Launches);
        Assert.Equal(2, statuses.Values.Count);
    }

    [Fact]
    public async Task TheActiveModel_IsNoneWithoutSettings_TheSetUpOneBeforeItLoads_AndTheLoadedOneAfter()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);

        Assert.Null(await CreateService(lifecycle, null).GetActiveModelAsync(TestPipes.Timeout()));

        var models = CreateService(lifecycle, setup.ScenarioPath);
        var before = await models.GetActiveModelAsync(TestPipes.Timeout());
        Assert.Equal(0, host.Launcher.Started);
        await ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout()));
        var after = await models.GetActiveModelAsync(TestPipes.Timeout());

        // Before its first use the model is described by its settings; once loaded, by what the engine started with.
        Assert.Equal(new ModelInfo("model", ModelFiles.DefaultContextLength) { SupportsConstrainedOutput = true, SupportsToolCalling = true }, before);
        Assert.Equal(new ModelInfo("model", 2048) { SupportsConstrainedOutput = true, SupportsToolCalling = true }, after);
    }

    [Fact]
    public async Task WithoutAModelSetUp_GeneratingFailsAsNotFound_AndStartsNothing()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario());
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);

        var failure = await Assert.ThrowsAsync<ModelHostException>(
            () => ReadAllAsync(CreateService(lifecycle, null).GenerateAsync(Question, TestPipes.Timeout())));

        Assert.Equal(ModelHostErrorCode.ModelNotFound, failure.Code);
        Assert.Equal(0, host.Launcher.Started);
    }

    [Fact]
    public async Task TwoQuestionsAtOnce_LoadTheModelOnce_AndAreAnsweredInTurn()
    {
        using var setup = FakeEngineSetup.Create(
            new FakeEngineScenario { Chat = new FakeChatReply { Pieces = ["a", "b"], PieceDelayMs = 50 } });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = CreateService(lifecycle, setup.ScenarioPath);

        var answers = await Task.WhenAll(
            ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout())),
            ReadAllAsync(models.GenerateAsync(Question, TestPipes.Timeout())));

        Assert.All(answers, answer => Assert.Equal("ab", string.Concat(answer.Select(chunk => chunk.Text))));
        Assert.Equal(1, setup.Launches);
    }

    [Fact]
    public async Task StoppingAnAnswer_StopsTheEngine_AndTheNextQuestionIsAnsweredAtOnce()
    {
        using var setup = FakeEngineSetup.Create(
            new FakeEngineScenario { Chat = new FakeChatReply { Pieces = [.. Enumerable.Repeat("word ", 100)], PieceDelayMs = 50 } });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = CreateService(lifecycle, setup.ScenarioPath);

        // Leaving the stream after two pieces, as the conversation does when the user stops the answer.
        var pieces = 0;
        await foreach (var _ in models.GenerateAsync(Question, TestPipes.Timeout()))
        {
            if (++pieces == 2)
            {
                break;
            }
        }

        await WaitUntilAsync(() => File.Exists(FakeChatReply.DisconnectedPath(setup.ScenarioPath, 1)));

        // And cancelling the next one, then asking again: never refused as busy.
        using var cancel = new CancellationTokenSource();
        var cancelled = Task.Run(async () =>
        {
            await foreach (var _ in models.GenerateAsync(Question, cancel.Token))
            {
                await cancel.CancelAsync();
            }
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        // The cancellation reached the engine: it stopped its work on that answer too.
        await WaitUntilAsync(() => File.Exists(FakeChatReply.DisconnectedPath(setup.ScenarioPath, 2)));

        await foreach (var chunk in models.GenerateAsync(Question, TestPipes.Timeout()))
        {
            Assert.Equal("word ", chunk.Text);
            break;
        }

        Assert.Equal(1, setup.Launches);
    }

    [Fact]
    public async Task AnAnswerCutShort_EndsWithANotice_AndEngineErrorsFailWithTheirCode()
    {
        using var setup = FakeEngineSetup.Create(new FakeEngineScenario { Chat = new FakeChatReply { FinishReason = "length" } });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);

        var chunks = await ReadAllAsync(CreateService(lifecycle, setup.ScenarioPath).GenerateAsync(Question, TestPipes.Timeout()));

        Assert.Equal(AssistantResponseChunk.ForNotice(LocalModelService.OutputLimitNotice), chunks[^1]);

        var tooLong = setup.CreateScenario("long.gguf", new FakeEngineScenario { Chat = new FakeChatReply { Error = "context" } });
        await lifecycle.UnloadAsync(TestPipes.Timeout());
        var failure = await Assert.ThrowsAsync<ModelHostException>(
            () => ReadAllAsync(CreateService(lifecycle, tooLong).GenerateAsync(Question, TestPipes.Timeout())));
        Assert.Equal(ModelHostErrorCode.ContextExceeded, failure.Code);
        Assert.Contains("more than the local model can read", ModelErrorText.Describe(failure), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHostThatGoesAwayMidAnswer_FailsIt()
    {
        using var setup = FakeEngineSetup.Create(
            new FakeEngineScenario { Chat = new FakeChatReply { Pieces = [.. Enumerable.Repeat("word ", 100)], PieceDelayMs = 50 } });
        await using var host = new TestHost(setup);
        await using var lifecycle = host.CreateLifecycle(out _);
        var models = CreateService(lifecycle, setup.ScenarioPath);

        var failure = await Assert.ThrowsAsync<ModelHostException>(async () =>
        {
            await foreach (var _ in models.GenerateAsync(Question, TestPipes.Timeout()))
            {
                await host.Launcher.Last!.CrashAsync();
            }
        });

        Assert.Null(failure.Code);
        Assert.Null(lifecycle.Model);
    }

    private static LocalModelService CreateService(IModelLifecycle lifecycle, string? modelFile) =>
        new(lifecycle, new FixedSettings(new AppSettings { Model = new ModelSettings { ModelFilePath = modelFile } }),
            new NoModelProfiles(), NullLogger<LocalModelService>.Instance);

    private static async Task<List<AssistantResponseChunk>> ReadAllAsync(IAsyncEnumerable<AssistantResponseChunk> chunks)
    {
        var all = new List<AssistantResponseChunk>();
        await foreach (var chunk in chunks)
        {
            all.Add(chunk);
        }

        return all;
    }

    private sealed class FixedSettings(AppSettings settings) : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(settings);

        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
