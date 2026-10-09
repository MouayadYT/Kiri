using System.Collections.Concurrent;
using System.Diagnostics;
using Assistant.Core.Settings;
using Assistant.Core.Storage;
using Assistant.Core.Voice;
using Assistant.Voice;
using Xunit;
using Xunit.Abstractions;

namespace Assistant.Voice.Tests;

/// <summary>
/// The three voices, the recognizer and the wake-word listener, run for real on this PC from the files in the user's voices folder
/// (<c>%LOCALAPPDATA%\Assistant\voices</c>, or <c>ASSISTANT_TEST_VOICES</c>'s parent). They return at once, and say so, when a group of files is not there:
/// the models are never in the repository.
/// </summary>
public sealed class RealVoiceEngineTests(ITestOutputHelper output)
{
    private static readonly AppPaths Paths = new(
        Environment.GetEnvironmentVariable("ASSISTANT_TEST_VOICES") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppPaths.AppFolderName));

    private static readonly VoiceModelFolders Folders = new(Paths);

    private bool Have(params string[] groups)
    {
        var missing = groups.Where(group => !Folders.HasUserFiles(group)).ToArray();
        if (missing.Length > 0)
        {
            output.WriteLine("Skipped: no files for " + string.Join(", ", missing));
        }

        return missing.Length == 0;
    }

    // ---- text to speech ----

    [Theory]
    [InlineData("kitten-tts-mini", 24_000)]
    [InlineData("kokoro-82m-onnx", 24_000)]
    [InlineData("piper", 22_050)]
    public void EachEngineLoadsAndSpeaks(string engineId, int expectedRate)
    {
        if (!Have(engineId))
        {
            return;
        }

        using var engine = new TextToSpeechEngineFactory(Folders).Create(TextToSpeechModels.Find(engineId)!);
        var load = Stopwatch.StartNew();
        engine.Load();
        output.WriteLine($"{engineId}: loaded in {load.ElapsedMilliseconds} ms");
        var samples = 0;
        TimeSpan? first = null;
        var timer = Stopwatch.StartNew();

        engine.Synthesize(
            "Your next meeting is at three o'clock tomorrow afternoon.",
            block =>
            {
                first ??= timer.Elapsed;
                samples += block.Length;
                Assert.All(block.ToArray(), sample => Assert.InRange(sample, -1.01f, 1.01f));
                return true;
            },
            CancellationToken.None);

        output.WriteLine($"{engineId}: first audio {first!.Value.TotalMilliseconds:F0} ms, total {timer.ElapsedMilliseconds} ms, {samples / (double)engine.SampleRate:F2} s of speech");
        Assert.Equal(expectedRate, engine.SampleRate);
        Assert.InRange(samples / (double)engine.SampleRate, 2.0, 6.0);
        Assert.True(first.Value < timer.Elapsed + TimeSpan.FromMilliseconds(1));
    }

    [Theory]
    [InlineData("kitten-tts-mini")]
    [InlineData("kokoro-82m-onnx")]
    [InlineData("piper")]
    public void SpeechCanBeStoppedInTheMiddleOfAText(string engineId)
    {
        if (!Have(engineId))
        {
            return;
        }

        using var engine = new TextToSpeechEngineFactory(Folders).Create(TextToSpeechModels.Find(engineId)!);
        engine.Load();
        using var cancel = new CancellationTokenSource();
        var blocks = 0;

        // Two sentences: the first block is handed over after the first, and the stop comes before the second is made.
        Assert.ThrowsAny<OperationCanceledException>(() => engine.Synthesize(
            "This is the first sentence of the test. This is the second sentence, which must never be made.",
            _ =>
            {
                blocks++;
                cancel.Cancel();
                return true;
            },
            cancel.Token));

        Assert.Equal(1, blocks);
    }

    [Fact]
    public void AnEngineWhoseFilesAreNotThereSaysSoWithoutAPathInItsWords()
    {
        var missing = new VoiceModelFolders(new AppPaths(Path.Combine(Path.GetTempPath(), "assistant-no-voices-" + Guid.NewGuid().ToString("N"))));
        using var engine = new TextToSpeechEngineFactory(missing).Create(TextToSpeechModels.Piper);

        var exception = Assert.Throws<VoiceEngineException>(engine.Load);

        Assert.Equal(VoiceEngineFailure.NotInstalled, exception.Failure);
        Assert.DoesNotContain("assistant-no-voices", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheServiceSpeaksAnswersFromTheRealEnginesAndSwitchesBetweenThem()
    {
        if (!Have("kitten-tts-mini", "kokoro-82m-onnx", "piper"))
        {
            return;
        }

        var output2 = new CountingOutput();
        using var service = new TextToSpeechService(new TextToSpeechEngineFactory(Folders), output2);
        foreach (var model in TextToSpeechModels.All)
        {
            await service.SelectEngineAsync(model.Id);
            Assert.True(service.Status.IsReady, $"{model.Id}: {service.Status.Engine.Message}");

            var response = service.BeginResponse();
            var finished = new ManualResetEventSlim();
            response.Finished += (_, _) => finished.Set();
            response.Append("Sure, here is the answer. ");
            response.Append("It comes in two sentences.");
            response.Complete();

            Assert.True(finished.Wait(TimeSpan.FromSeconds(60)), $"{model.Id} did not finish");
            Assert.False(response.IsStopped);
            output.WriteLine($"{model.Id}: time to first audio {response.TimeToFirstAudio!.Value.TotalMilliseconds:F0} ms, {output2.Seconds:F1} s of speech so far");
        }

        Assert.True(output2.Seconds > 3);
    }

    [Fact]
    public async Task TheBenchmarkMeasuresEachRealEngine()
    {
        if (!Have("kitten-tts-mini", "kokoro-82m-onnx", "piper"))
        {
            return;
        }

        using var service = new TextToSpeechService(new TextToSpeechEngineFactory(Folders), new CountingOutput());
        foreach (var model in TextToSpeechModels.All)
        {
            await service.SelectEngineAsync(model.Id);
            var result = await service.BenchmarkAsync();

            Assert.Null(result.Failure);
            Assert.Equal(3, result.Samples.Count);
            output.WriteLine(
                $"{model.Id}: load {result.LoadTime?.TotalMilliseconds:F0} ms, shortest text first audio {result.ShortestTimeToFirstAudio.TotalMilliseconds:F0} ms, " +
                $"median {result.MedianTimeToFirstAudio.TotalMilliseconds:F0} ms, {result.SpeedMultiple:F1}x real time, {result.CharactersPerSecond:F0} characters/s");
            Assert.True(result.SpeedMultiple > 0.2);
        }
    }

    // ---- speech recognition ----

    [Fact]
    public async Task ASpokenRequestIsRecognizedWhileItIsSpokenAndEndsWhenTheSpeakerStops()
    {
        if (!Have("piper", "speech-recognition"))
        {
            return;
        }

        var audio = await Speech.SayAsync("What's on my calendar tomorrow?", leadIn: 0.6, leadOut: 2.0);
        using var service = new SherpaSpeechToTextService(Folders);
        await service.WarmUpAsync();
        Assert.True(service.Status.IsReady, service.Status.Message);
        using var session = service.StartSession(new SpeechRecognitionOptions { TrailingSilence = TimeSpan.FromSeconds(1) });
        var partials = new ConcurrentQueue<string>();
        var final = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, e) =>
        {
            if (e.IsFinal)
            {
                final.TrySetResult(e);
            }
            else
            {
                partials.Enqueue(e.Text);
            }
        };

        Speech.Feed(session, audio);
        var ended = await final.Task.WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine($"Heard: '{ended.Text}' after {partials.Count} partial updates; ended: {ended.Ended}");
        Assert.Equal(SpeechEndReason.Endpoint, ended.Ended);
        Assert.Contains("calendar", ended.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(partials.Count >= 2, "The words should arrive while the request is being spoken.");
        Assert.Equal("What's on my calendar tomorrow?", SpokenRequestText.Normalize(ended.Text), ignoreCase: true);
    }

    [Fact]
    public async Task ALongRequestIsNotCutOffInTheMiddleAndEndsAfterItsLastWord()
    {
        if (!Have("piper", "speech-recognition"))
        {
            return;
        }

        const string said = "Can you please tell me what the weather is going to be like in the morning and then remind me to bring an umbrella to the office if it rains before lunch";
        var audio = await Speech.SayAsync(said, leadIn: 0.5, leadOut: 2.5);
        using var service = new SherpaSpeechToTextService(Folders);
        using var session = service.StartSession();
        var final = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, e) =>
        {
            if (e.IsFinal)
            {
                final.TrySetResult(e);
            }
        };

        Speech.Feed(session, audio);
        var ended = await final.Task.WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine($"{audio.Length / (double)VoiceAudio.SampleRate:F1} s of audio; heard: '{ended.Text}'");
        Assert.Equal(SpeechEndReason.Endpoint, ended.Ended);
        Assert.Contains("umbrella", ended.Text, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("lunch", ended.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SilenceEndsTheSessionWithNoSpeechAndNoWords()
    {
        if (!Have("speech-recognition"))
        {
            return;
        }

        using var service = new SherpaSpeechToTextService(Folders);
        using var session = service.StartSession(new SpeechRecognitionOptions { InitialSilence = TimeSpan.FromSeconds(2) });
        var final = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, e) =>
        {
            if (e.IsFinal)
            {
                final.TrySetResult(e);
            }
        };

        Speech.Feed(session, new short[VoiceAudio.SamplesIn(TimeSpan.FromSeconds(4))]);
        var ended = await final.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(SpeechEndReason.NoSpeech, ended.Ended);
        Assert.Equal("", ended.Text);
    }

    [Fact]
    public async Task FinishingASessionRecognizesWhatWasSaidSoFar()
    {
        if (!Have("piper", "speech-recognition"))
        {
            return;
        }

        var audio = await Speech.SayAsync("Show my calendar please.", leadIn: 0.3, leadOut: 0);
        using var service = new SherpaSpeechToTextService(Folders);
        using var session = service.StartSession();
        var final = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, e) =>
        {
            if (e.IsFinal)
            {
                final.TrySetResult(e);
            }
        };

        Speech.Feed(session, audio);
        session.Finish();
        var ended = await final.Task.WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine($"Heard: '{ended.Text}'");
        Assert.Equal(SpeechEndReason.Finished, ended.Ended);
        Assert.Contains("calendar", ended.Text, StringComparison.OrdinalIgnoreCase);
        Assert.True(session.IsEnded);
    }

    [Fact]
    public async Task WordsBeforeTheCutAreLeftOut()
    {
        if (!Have("piper", "speech-recognition"))
        {
            return;
        }

        // "Kiri." is said in the first 0.7 seconds after the lead-in, and the request after it.
        var cut = TimeSpan.FromSeconds(0.75);
        var audio = await Speech.SayAsync("Kiri. What time is it?", leadIn: 0.3, leadOut: 1.5);
        using var service = new SherpaSpeechToTextService(Folders);
        using var session = service.StartSession(new SpeechRecognitionOptions { IgnoreWordsBefore = cut });
        var final = new TaskCompletionSource<SpeechTranscriptEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Transcribed += (_, e) =>
        {
            output.WriteLine($"  {(e.IsFinal ? "final" : "partial")}: '{e.Text}'");
            if (e.IsFinal)
            {
                final.TrySetResult(e);
            }
        };

        Speech.Feed(session, audio);
        var ended = await final.Task.WaitAsync(TimeSpan.FromSeconds(30));

        output.WriteLine($"Heard after the cut: '{ended.Text}'");
        Assert.Contains("time", ended.Text, StringComparison.OrdinalIgnoreCase);
    }

    // ---- wake word ----

    [Fact]
    public async Task TheWakeWordIsHeardAfterAPauseAndOrdinarySpeechIsNot()
    {
        if (!Have("piper", "kitten-tts-mini", "wake-word"))
        {
            return;
        }

        using var service = new SherpaWakeWordService(Folders, Paths);
        await service.WarmUpAsync();
        Assert.True(service.Status.IsReady, service.Status.Message);

        var heard = new List<string>();
        foreach (var phrase in new[] { "Keeree.", "Keeree, what's on my calendar tomorrow?", "Keeri, open the settings.", "Kiri, what time is it?" })
        {
            var audio = await Speech.SayAsync(phrase, leadIn: 1.0, leadOut: 1.5, TextToSpeechModels.KittenTtsMini);
            if (await DetectsAsync(service, audio))
            {
                heard.Add(phrase);
            }
        }

        output.WriteLine("Heard the wake word in: " + string.Join(" | ", heard));
        Assert.NotEmpty(heard);

        foreach (var phrase in new[]
        {
            "The weather is nice today and I think we should go for a walk.",
            "Please remind me to call my brother about the exam schedule.",
            "Your next meeting is at three o'clock tomorrow afternoon.",
            "Siri, what's the weather like today?",
            "I'd like a cup of coffee and a slice of cake, thank you.",
        })
        {
            var audio = await Speech.SayAsync(phrase, leadIn: 1.0, leadOut: 1.5);
            Assert.False(await DetectsAsync(service, audio), $"'{phrase}' woke the Assistant.");
        }
    }

    [Fact]
    public async Task HeyKiriWakesTheAssistantAndSimilarGreetingsToSomeoneElseDoNot()
    {
        if (!Have("piper", "kitten-tts-mini", "wake-word"))
        {
            return;
        }

        using var service = new SherpaWakeWordService(Folders, Paths);
        await service.WarmUpAsync();
        Assert.True(service.Status.IsReady, service.Status.Message);

        // "Hey" is as loud as the word, so a listener that needs the word to rise out of a pause never hears it said this way.
        foreach (var voice in new[] { TextToSpeechModels.Piper, TextToSpeechModels.KittenTtsMini })
        {
            foreach (var phrase in new[] { "Hey Kiri.", "Hey, Kiri.", "Hey Keeree, what's on my calendar tomorrow?" })
            {
                var audio = await Speech.SayAsync(phrase, leadIn: 1.0, leadOut: 1.5, voice);
                Assert.True(await DetectsAsync(service, audio), $"'{phrase}' in {voice.Id} did not wake the Assistant.");
            }
        }

        foreach (var phrase in new[]
        {
            "Hey there, how are you doing today?",
            "Hey Siri, set a timer for ten minutes.",
            "Okay Google, play some music.",
            "Hey Kerry, can you pass me the salt?",
            "Hey everyone, welcome back to the channel.",
        })
        {
            var audio = await Speech.SayAsync(phrase, leadIn: 1.0, leadOut: 1.5);
            Assert.False(await DetectsAsync(service, audio), $"'{phrase}' woke the Assistant.");
        }
    }

    [Fact]
    public async Task TheWakeWordWakesOncePerUtterance()
    {
        if (!Have("kitten-tts-mini", "wake-word"))
        {
            return;
        }

        using var service = new SherpaWakeWordService(Folders, Paths, new WakeWordTuning { Cooldown = TimeSpan.FromSeconds(5) });
        var audio = await Speech.SayAsync("Keeree. Keeree.", leadIn: 1.0, leadOut: 1.5, TextToSpeechModels.KittenTtsMini);
        using var session = service.StartSession();
        var count = 0;
        session.Detected += (_, _) => Interlocked.Increment(ref count);

        Speech.Feed(session, audio);
        await Task.Delay(1500);

        Assert.True(count <= 1, $"The wake word woke the Assistant {count} times for one utterance.");
    }

    [Fact]
    public async Task KiriAndHeyKiriWakeTheAssistantInKokorosVoice_AndNamesThatSoundLikeItDoNot()
    {
        if (!Have("kokoro-82m-onnx", "wake-word"))
        {
            return;
        }

        // The tuning measured with this voice, which is the one that is on this PC: "Keeree" and "Kerry" are left to the listener's judgement, and a
        // name said to someone else ("Hey Kerry"), another assistant's name and ordinary talk never wake it.
        using var service = new SherpaWakeWordService(Folders, Paths);
        await service.WarmUpAsync();
        Assert.True(service.Status.IsReady, service.Status.Message);
        foreach (var phrase in new[] { "Kiri.", "Hey Kiri.", "Kiri, what time is it?", "Okay Kiri, set a timer." })
        {
            var audio = await Speech.SayAsync(phrase, leadIn: 1.0, leadOut: 1.5, TextToSpeechModels.Kokoro);
            Assert.True(await DetectsAsync(service, audio), $"'{phrase}' did not wake the Assistant.");
        }

        foreach (var phrase in new[] { "Kerry.", "Hey Kerry, pass the salt.", "Hey Siri, set a timer.", "Okay Google, play some music.", "The weather is nice today and I think we should go for a walk." })
        {
            var audio = await Speech.SayAsync(phrase, leadIn: 1.0, leadOut: 1.5, TextToSpeechModels.Kokoro);
            Assert.False(await DetectsAsync(service, audio), $"'{phrase}' woke the Assistant.");
        }
    }

    [Fact]
    public async Task TheListenerSaysHowItIsDoingWithoutSayingWhatItHeard()
    {
        if (!Have("kokoro-82m-onnx", "wake-word"))
        {
            return;
        }

        var lines = new CollectingLogger();
        using var service = new SherpaWakeWordService(
            Folders, Paths, new WakeWordTuning { FirstReport = TimeSpan.FromSeconds(1), ReportEvery = TimeSpan.FromSeconds(1) }, lines);
        await service.WarmUpAsync();
        var audio = await Speech.SayAsync("Hey Kiri.", leadIn: 1.0, leadOut: 1.5, TextToSpeechModels.Kokoro);

        Assert.True(await DetectsAsync(service, audio));

        // How long it listened, how much of that was louder than the room, and the loudest it got: numbers, and no words.
        var reports = lines.Messages.Where(message => message.StartsWith("The wake word listener heard", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(reports);
        Assert.All(reports, report =>
        {
            Assert.Matches(@"heard \d+ s, \d+ s of it above the noise, loudest \d+ %", report);
            Assert.DoesNotContain("Kiri", report, StringComparison.OrdinalIgnoreCase);
        });
    }

    private sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger<SherpaWakeWordService>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }

    private static async Task<bool> DetectsAsync(SherpaWakeWordService service, short[] audio)
    {
        using var session = service.StartSession();
        var detected = new ManualResetEventSlim();
        session.Detected += (_, _) => detected.Set();
        Speech.Feed(session, audio);
        return await Task.Run(() => detected.Wait(TimeSpan.FromSeconds(3)));
    }

    // ---- helpers ----

    private sealed class CountingOutput : IAudioOutput
    {
        private long _samples;
        private int _rate = 1;

        public double Seconds => Interlocked.Read(ref _samples) / (double)_rate;

        public IAudioOutputStream Open(int sampleRate)
        {
            _rate = sampleRate;
            return new Stream(this);
        }

        private sealed class Stream(CountingOutput owner) : IAudioOutputStream
        {
            public TimeSpan Queued => TimeSpan.Zero;

            public void Write(ReadOnlySpan<float> samples) => Interlocked.Add(ref owner._samples, samples.Length);

            public void Clear()
            {
            }

            public Task WaitUntilDrainedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public void Dispose()
            {
            }
        }
    }

    // Speech made by the Piper voice, as the microphone would deliver it: 16 kHz mono 16-bit, with silence (and a little noise) around it.
    private static class Speech
    {
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static readonly Dictionary<string, ITextToSpeechEngine> Engines = [];

        public static async Task<short[]> SayAsync(string text, double leadIn, double leadOut, TextToSpeechModel? voice = null)
        {
            await Gate.WaitAsync();
            try
            {
                var engine = Engines.TryGetValue((voice ?? TextToSpeechModels.Piper).Id, out var known) ? known : LoadEngine(voice ?? TextToSpeechModels.Piper);
                var samples = new List<float>();
                engine.Synthesize(text, block =>
                {
                    samples.AddRange(block.ToArray());
                    return true;
                }, CancellationToken.None);
                return Resample(samples, engine.SampleRate, leadIn, leadOut);
            }
            finally
            {
                Gate.Release();
            }
        }

        public static void Feed(ISpeechRecognitionSession session, short[] audio)
        {
            for (var i = 0; i < audio.Length; i += 1600)
            {
                session.Push(audio.AsSpan(i, Math.Min(1600, audio.Length - i)));
            }
        }

        public static void Feed(IWakeWordSession session, short[] audio)
        {
            for (var i = 0; i < audio.Length; i += 160)
            {
                session.Push(audio.AsSpan(i, Math.Min(160, audio.Length - i)));
            }
        }

        private static ITextToSpeechEngine LoadEngine(TextToSpeechModel model)
        {
            var engine = new TextToSpeechEngineFactory(Folders).Create(model);
            engine.Load();
            Engines[model.Id] = engine;
            return engine;
        }

        private static short[] Resample(List<float> source, int rate, double leadIn, double leadOut)
        {
            var random = new Random(7);
            var count = (int)((long)source.Count * VoiceAudio.SampleRate / rate);
            var lead = (int)(leadIn * VoiceAudio.SampleRate);
            var tail = (int)(leadOut * VoiceAudio.SampleRate);
            var result = new short[lead + count + tail];
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (short)random.Next(-20, 21);
            }

            for (var i = 0; i < count; i++)
            {
                var position = (double)i * rate / VoiceAudio.SampleRate;
                var index = (int)position;
                var next = Math.Min(index + 1, source.Count - 1);
                var value = (source[index] * (1 - (position - index))) + (source[next] * (position - index));
                result[lead + i] = (short)Math.Clamp((value * 30000) + random.Next(-20, 21), short.MinValue, short.MaxValue);
            }

            return result;
        }
    }
}
