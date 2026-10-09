using System.Collections.Concurrent;
using System.Diagnostics;
using Assistant.Core.Settings;
using Assistant.Core.Voice;
using Xunit;

namespace Assistant.Core.Tests;

public sealed class TextToSpeechServiceTests : IDisposable
{
    private readonly FakeEngineFactory _factory = new();
    private readonly FakeOutput _output = new();
    private readonly TextToSpeechService _service;

    public TextToSpeechServiceTests() =>
        _service = new TextToSpeechService(_factory, _output, new TextToSpeechServiceOptions { IdleUnloadAfter = TimeSpan.FromHours(1) });

    public void Dispose() => _service.Dispose();

    private static bool Eventually(Func<bool> condition, int milliseconds = 5000)
    {
        var timer = Stopwatch.StartNew();
        while (timer.ElapsedMilliseconds < milliseconds)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    [Fact]
    public async Task UnloadingReleasesTheVoiceForDeletionAndTheNextResponseCanLoadItAgain()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var previous = _factory.Engine!;
        var response = _service.BeginResponse();
        response.Append("This voice is speaking before deletion. ");
        Assert.True(Eventually(() => _output.Stream?.Writes > 0));
        await _service.UnloadAsync();
        Assert.True(previous.IsDisposed);
        Assert.True(response.IsStopped);
        Assert.False(_service.Status.IsReady);
        await _service.WarmUpAsync();
        Assert.True(_service.Status.IsReady);
        Assert.NotSame(previous, _factory.Engine);
    }

    [Fact]
    public async Task TheVoiceStartsBeforeTheAnswerIsFinished()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();

        response.Append("You have two meetings tomorrow. The first is the design");

        // The answer is still being written, and the first sentence is already being said.
        Assert.True(Eventually(() => _output.Stream?.Writes > 0));
        Assert.Equal(["You have two meetings tomorrow."], _factory.Engine!.Spoken);
        Assert.False(response.IsFinished);
        Assert.True(_service.IsSpeaking);
    }

    [Fact]
    public async Task TheWriterIsNeverHeldUpByTheVoice()
    {
        _factory.SynthesisDelay = TimeSpan.FromMilliseconds(400);
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();

        var timer = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++)
        {
            response.Append("This is a sentence that is long enough to be said by itself. ");
        }

        response.Complete();

        Assert.True(timer.ElapsedMilliseconds < 200, $"Appending took {timer.ElapsedMilliseconds} ms");
        response.Stop();
    }

    [Fact]
    public async Task StoppingASpokenResponseSilencesItAndIgnoresWhatComesLater()
    {
        _factory.SynthesisDelay = TimeSpan.FromMilliseconds(50);
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();
        var finished = 0;
        response.Finished += (_, _) => Interlocked.Increment(ref finished);
        response.Append("This is the first sentence of the answer. This is the second sentence of the answer. ");
        Assert.True(Eventually(() => _output.Stream?.Writes > 0));

        response.Stop();
        var spokenAtStop = _factory.Engine!.Spoken.Count;
        response.Append("This is a later sentence that must never be said aloud. ");
        response.Complete();
        Thread.Sleep(250);

        Assert.True(response.IsStopped);
        Assert.True(response.IsFinished);
        Assert.False(_service.IsSpeaking);
        Assert.Equal(1, Volatile.Read(ref finished));
        Assert.True(_output.Stream!.Clears >= 1);
        Assert.Equal(spokenAtStop, _factory.Engine.Spoken.Count);
        Assert.DoesNotContain(_factory.Engine.Spoken, text => text.Contains("later", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NothingReachesTheSpeakersAfterTheyWereCleared()
    {
        // Many small blocks per sentence, so a stop is likely to come in the middle of one.
        _factory.BlocksPerText = 200;
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();
        response.Append("A sentence that the voice is making when it is stopped. ");
        Assert.True(Eventually(() => _output.Stream?.Writes > 3));

        response.Stop();
        var after = _output.Stream!.WritesAfterLastClear;
        Thread.Sleep(150);

        Assert.Equal(after, _output.Stream.WritesAfterLastClear);
        Assert.Equal(0, _output.Stream.WritesAfterLastClear);
    }

    [Fact]
    public async Task AWholeResponseFinishesWhenItsSoundHasBeenPlayed()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();
        var finished = new ManualResetEventSlim();
        response.Finished += (_, _) => finished.Set();

        response.Append("One short answer for you today.");
        response.Complete();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(response.IsStopped);
        Assert.False(_service.IsSpeaking);
        Assert.NotNull(response.TimeToFirstAudio);
    }

    [Fact]
    public async Task AResponseWithNothingToSayFinishesAtOnce()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();

        response.Append("```\ncode only\n```");
        response.Complete();

        Assert.True(Eventually(() => response.IsFinished));
        Assert.Empty(_factory.Engine!.Spoken);
    }

    [Fact]
    public async Task ANewResponseTakesOverFromTheOneBeforeIt()
    {
        _factory.SynthesisDelay = TimeSpan.FromMilliseconds(30);
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var first = _service.BeginResponse();
        first.Append("The first answer goes on for quite a long time indeed. And on. ");
        Assert.True(Eventually(() => _output.Stream?.Writes > 0));

        var second = _service.BeginResponse();

        Assert.True(first.IsStopped);
        Assert.False(second.IsStopped);
        second.Append("The second answer replaces it completely and is spoken. ");
        Assert.True(Eventually(() => _factory.Engine!.Spoken.Any(text => text.StartsWith("The second", StringComparison.Ordinal))));
    }

    [Fact]
    public async Task StopAllSilencesWhateverIsBeingSaid()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();
        response.Append("A sentence that is being said right now. ");
        Assert.True(Eventually(() => _output.Stream?.Writes > 0));
        var changes = 0;
        _service.SpeakingChanged += (_, _) => Interlocked.Increment(ref changes);

        _service.StopAll();

        Assert.True(response.IsStopped);
        Assert.False(_service.IsSpeaking);
        Assert.True(_output.Stream!.Clears >= 1);
        Assert.True(Volatile.Read(ref changes) >= 1);
    }

    [Fact]
    public async Task ChoosingAnotherEngineStopsSpeechLoadsTheNewOneAndLetsGoOfTheOld()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var piper = _factory.Engine!;
        var response = _service.BeginResponse();
        response.Append("A sentence being said while the engine is changed. ");
        Assert.True(Eventually(() => _output.Stream?.Writes > 0));
        var statuses = new ConcurrentQueue<VoiceEngineState>();
        _service.StatusChanged += (_, _) => statuses.Enqueue(_service.Status.Engine.State);

        await _service.SelectEngineAsync(TextToSpeechModels.Kokoro.Id);

        Assert.True(response.IsStopped);
        Assert.Equal(TextToSpeechModels.Kokoro.Id, _service.Status.EngineId);
        Assert.True(_service.Status.IsReady);
        Assert.NotSame(piper, _factory.Engine);
        Assert.Equal(TextToSpeechModels.Kokoro.Id, _factory.Engine!.Model.Id);
        Assert.True(Eventually(() => piper.IsDisposed));
        Assert.Contains(VoiceEngineState.Loading, statuses);
        Assert.Contains(VoiceEngineState.Ready, statuses);
    }

    [Fact]
    public async Task AnEngineThatIsNotInstalledSaysSoAndDoesNotSpeak()
    {
        _factory.FailWith = new VoiceEngineException(VoiceEngineFailure.NotInstalled, "This voice isn't installed.");

        await _service.SelectEngineAsync(TextToSpeechModels.Kokoro.Id);

        Assert.Equal(VoiceEngineState.NotInstalled, _service.Status.Engine.State);
        Assert.Equal("This voice isn't installed.", _service.Status.Engine.Message);

        var response = _service.BeginResponse();
        response.Append("This cannot be said because there is no voice. ");
        response.Complete();
        Assert.True(Eventually(() => response.IsFinished));
        Assert.True(response.IsStopped);
        Assert.Equal(0, _output.Opened);
    }

    [Fact]
    public async Task AnEngineThatFailsToLoadIsReportedFailedAndTriedAgainLater()
    {
        _factory.FailWith = new VoiceEngineException(VoiceEngineFailure.LoadFailed, "The voice could not be loaded from its files.");
        await _service.SelectEngineAsync(TextToSpeechModels.KittenTtsMini.Id);
        Assert.Equal(VoiceEngineState.Failed, _service.Status.Engine.State);

        _factory.FailWith = null;
        await _service.WarmUpAsync();

        Assert.True(_service.Status.IsReady);
    }

    [Fact]
    public async Task SelectingTheSameEngineAgainChangesNothing()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var engine = _factory.Engine;

        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);

        Assert.Same(engine, _factory.Engine);
        Assert.Equal(1, _factory.Created);
    }

    [Fact]
    public async Task SelectingWithoutLoadingWaitsUntilTheEngineIsNeeded()
    {
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id, load: false);

        Assert.Equal(0, _factory.Created);
        Assert.Equal(VoiceEngineState.NotLoaded, _service.Status.Engine.State);

        _ = _service.BeginResponse();

        Assert.True(Eventually(() => _service.Status.IsReady));
    }

    [Fact]
    public async Task AnUnknownEngineIsRefused() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _service.SelectEngineAsync("no-such-engine"));

    [Fact]
    public async Task TheBenchmarkMeasuresTheChosenEngineWithoutPlayingIt()
    {
        _factory.SynthesisDelay = TimeSpan.FromMilliseconds(20);
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);

        var result = await _service.BenchmarkAsync();

        Assert.Equal(TextToSpeechModels.Piper.Id, result.EngineId);
        Assert.True(result.HasSamples);
        Assert.Null(result.Failure);
        Assert.All(result.Samples, sample => Assert.True(sample.AudioDuration > TimeSpan.Zero));
        Assert.True(result.SpeedMultiple > 0);
        Assert.True(result.MedianTimeToFirstAudio > TimeSpan.Zero);
        Assert.Equal(0, _output.Opened);
    }

    [Fact]
    public async Task TheBenchmarkOfAnEngineThatCannotLoadSaysWhy()
    {
        _factory.FailWith = new VoiceEngineException(VoiceEngineFailure.NotInstalled, "This voice isn't installed.");
        await _service.SelectEngineAsync(TextToSpeechModels.Kokoro.Id);

        var result = await _service.BenchmarkAsync();

        Assert.False(result.HasSamples);
        Assert.Equal("This voice isn't installed.", result.Failure);
    }

    [Fact]
    public async Task ANoOutputDeviceLeavesTheAnswerUnspokenWithoutBlamingTheVoice()
    {
        _output.Fail = true;
        await _service.SelectEngineAsync(TextToSpeechModels.Piper.Id);
        var response = _service.BeginResponse();

        response.Append("A sentence that has nowhere to be played at all. ");

        Assert.True(Eventually(() => response.IsStopped));
        Assert.True(_service.Status.IsReady);
    }

    // ---- fakes ----

    private sealed class FakeEngineFactory : ITextToSpeechEngineFactory
    {
        public TimeSpan SynthesisDelay { get; set; }

        public int BlocksPerText { get; set; } = 3;

        public VoiceEngineException? FailWith { get; set; }

        public FakeEngine? Engine { get; private set; }

        public int Created { get; private set; }

        public ITextToSpeechEngine Create(TextToSpeechModel model)
        {
            Created++;
            return Engine = new FakeEngine(model, this);
        }
    }

    private sealed class FakeEngine(TextToSpeechModel model, FakeEngineFactory factory) : ITextToSpeechEngine
    {
        private readonly List<string> _spoken = [];

        public TextToSpeechModel Model { get; } = model;

        public int SampleRate => 22_050;

        public bool IsDisposed { get; private set; }

        public IReadOnlyList<string> Spoken
        {
            get
            {
                lock (_spoken)
                {
                    return [.. _spoken];
                }
            }
        }

        public void Load()
        {
            if (factory.FailWith is { } failure)
            {
                throw failure;
            }
        }

        public void Synthesize(string text, SpeechAudioHandler onAudio, CancellationToken cancellationToken)
        {
            if (text != "Hello.")
            {
                lock (_spoken)
                {
                    _spoken.Add(text);
                }
            }

            var block = new float[441];
            for (var i = 0; i < factory.BlocksPerText; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (factory.SynthesisDelay > TimeSpan.Zero)
                {
                    Thread.Sleep(factory.SynthesisDelay / factory.BlocksPerText);
                }

                if (!onAudio(block))
                {
                    throw new OperationCanceledException();
                }
            }
        }

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeOutput : IAudioOutput
    {
        public FakeStream? Stream { get; private set; }

        public int Opened { get; private set; }

        public bool Fail { get; set; }

        public IAudioOutputStream Open(int sampleRate)
        {
            if (Fail)
            {
                throw new InvalidOperationException("No speakers.");
            }

            Opened++;
            return Stream = new FakeStream();
        }
    }

    private sealed class FakeStream : IAudioOutputStream
    {
        private int _writes;
        private int _clears;
        private int _afterClear;

        public int Writes => Volatile.Read(ref _writes);

        public int Clears => Volatile.Read(ref _clears);

        public int WritesAfterLastClear => Volatile.Read(ref _afterClear);

        public TimeSpan Queued => TimeSpan.Zero;

        public void Write(ReadOnlySpan<float> samples)
        {
            Interlocked.Increment(ref _writes);
            Interlocked.Increment(ref _afterClear);
        }

        public void Clear()
        {
            Interlocked.Increment(ref _clears);
            Interlocked.Exchange(ref _afterClear, 0);
        }

        public Task WaitUntilDrainedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
