using Assistant.Core.Voice;
using Assistant.UI.Voice;
using Assistant.Windows.Audio;
using Xunit;

namespace Assistant.UI.Tests;

// The one place that holds the microphone for voice (PROJECT_SPEC §4.2, step 125): it is open only while somebody needs it, its audio goes to the wake-word
// listener or to the request being recognized and never to both, and "Kiri, what's on my calendar?" loses nothing between the word and the request.
public sealed class MicrophoneRouterTests
{
    private const int Rate = VoiceAudio.SampleRate;

    // Numbered audio: sample i of the whole run is i modulo 30000, so a test can see what went where and in what order.
    private static short[] Block(ref int next, int count)
    {
        var block = new short[count];
        for (var i = 0; i < count; i++)
        {
            block[i] = (short)(next++ % 30000);
        }

        return block;
    }

    private static (MicrophoneRouter Router, FakeMicrophoneSource Microphone, FakeRecognizer Recognizer, FakeWakeWord Wake) Create()
    {
        var microphone = new FakeMicrophoneSource();
        var recognizer = new FakeRecognizer();
        var wake = new FakeWakeWord();
        return (new MicrophoneRouter(microphone, recognizer, wake), microphone, recognizer, wake);
    }

    [Fact]
    public void DisablingVoiceControlClosesTheMicrophoneAndPreventsWakeListening()
    {
        var (router, microphone, _, _) = Create();
        using (router)
        {
            router.SetWakeWord(true);
            using var request = router.Listen(new(), _ => { });
            router.SetVoiceInputEnabled(false);
            Assert.False(router.IsEnabled);
            Assert.False(router.IsListeningToRequest);
            Assert.False(router.IsListeningForWakeWord);
            Assert.True(microphone.WaitUntilClosed());
            var subscriptions = microphone.Subscriptions;
            router.SetWakeWord(true);
            Assert.Equal(subscriptions, microphone.Subscriptions);
            router.SetVoiceInputEnabled(true);
            router.SetWakeWord(true);
            Assert.True(router.IsListeningForWakeWord);
        }
    }

    [Fact]
    public void TheMicrophoneIsClosedUntilSomebodyNeedsIt()
    {
        var (router, microphone, _, _) = Create();

        Assert.Equal(0, microphone.Subscriptions);
        router.SetWakeWord(true);
        Assert.Equal(1, microphone.Subscriptions);
        Assert.True(router.IsListeningForWakeWord);
        router.SetWakeWord(false);
        Assert.True(microphone.WaitUntilClosed());
        Assert.False(router.IsListeningForWakeWord);
    }

    [Fact]
    public void AudioGoesToTheWakeListenerWhileNothingIsBeingAsked()
    {
        var (router, microphone, _, wake) = Create();
        router.SetWakeWord(true);
        var next = 0;

        microphone.Deliver(Block(ref next, 1600));
        microphone.Deliver(Block(ref next, 1600));

        Assert.Equal(3200, wake.Session!.Pushed.Count);
        Assert.Equal(0, wake.Session.Pushed[0]);
    }

    [Fact]
    public void ARequestOpensTheMicrophoneAndClosesItWhenItEnds()
    {
        var (router, microphone, recognizer, _) = Create();

        var listening = router.Listen(new VoiceListenRequest(), _ => { });
        Assert.Equal(1, microphone.Subscriptions);
        var next = 0;
        microphone.Deliver(Block(ref next, 1600));

        Assert.Equal(1600, recognizer.Sessions[0].Pushed.Count);
        Assert.True(router.IsListeningToRequest);
        listening.Dispose();
        Assert.True(microphone.WaitUntilClosed());
        Assert.False(router.IsListeningToRequest);
        Assert.True(recognizer.Sessions[0].IsDisposed);
        Assert.NotNull(router.LastRequestEndedAt);
    }

    [Fact]
    public void TheWakeWordHandsTheAudioAroundItToTheRequestWithoutALostWord()
    {
        var (router, microphone, recognizer, wake) = Create();
        router.SetWakeWord(true);
        WakeHandoff? handoff = null;
        router.WakeWordHeard += (_, heard) => handoff = heard;
        var next = 0;

        // Four seconds of audio go by; the listener decides that it heard the word.
        for (var i = 0; i < 40; i++)
        {
            microphone.Deliver(Block(ref next, 1600));
        }

        wake.Session!.Detect();
        Assert.NotNull(handoff);
        Assert.Equal(TimeSpan.FromSeconds(1.6), handoff!.SeedDuration);

        // Audio goes on arriving while the window opens and the request is being started: none of it may be lost.
        microphone.Deliver(Block(ref next, 1600));
        microphone.Deliver(Block(ref next, 800));
        var listening = router.Listen(new VoiceListenRequest { Handoff = handoff }, _ => { });
        microphone.Deliver(Block(ref next, 1600));

        var session = recognizer.Sessions.Single();
        var heard = session.Pushed;
        Assert.Equal(25600 + 1600 + 800 + 1600, heard.Count);
        var firstOfSeed = 40 * 1600 - 25600;
        for (var i = 0; i < heard.Count; i++)
        {
            Assert.Equal((short)((firstOfSeed + i) % 30000), heard[i]);
        }

        // The wake word itself is told to the recognizer, so that it is left out of the request.
        Assert.Equal(TimeSpan.FromSeconds(1.6 - 0.35), session.Options!.IgnoreWordsBefore);

        // While the request is heard, the wake-word listener hears nothing.
        var pushedToWake = wake.Session.Pushed.Count;
        microphone.Deliver(Block(ref next, 1600));
        Assert.Equal(pushedToWake, wake.Session.Pushed.Count);
        Assert.Equal(1, wake.Session.ResetCount);
        listening.Dispose();
    }

    [Fact]
    public void ARequestThatDidNotFollowTheWakeWordStartsWithTheLiveAudioAndNothingIsIgnored()
    {
        var (router, microphone, recognizer, _) = Create();
        var next = 0;
        microphone.Deliver(Block(ref next, 1600));

        _ = router.Listen(new VoiceListenRequest(), _ => { });
        microphone.Deliver(Block(ref next, 1600));

        Assert.Equal(TimeSpan.Zero, recognizer.Sessions.Single().Options!.IgnoreWordsBefore);
        Assert.Equal(1600, recognizer.Sessions.Single().Pushed.Count);
    }

    [Fact]
    public void TheWakeWordIsNotHeardWhileARequestIsBeingRecognized()
    {
        var (router, microphone, _, wake) = Create();
        router.SetWakeWord(true);
        WakeHandoff? handoff = null;
        router.WakeWordHeard += (_, heard) => handoff = heard;
        _ = router.Listen(new VoiceListenRequest(), _ => { });

        wake.Session!.Detect();

        Assert.Null(handoff);
    }

    [Fact]
    public void WhenTheRequestEndsTheWakeWordListensAgainFromAFreshStart()
    {
        var (router, microphone, _, wake) = Create();
        router.SetWakeWord(true);
        var next = 0;
        microphone.Deliver(Block(ref next, 1600));
        var listening = router.Listen(new VoiceListenRequest(), _ => { });
        microphone.Deliver(Block(ref next, 1600));
        var pushedWhileListening = wake.Session!.Pushed.Count;

        listening.Dispose();
        Assert.Equal(1, microphone.Subscriptions);
        microphone.Deliver(Block(ref next, 800));

        Assert.Equal(pushedWhileListening + 800, wake.Session.Pushed.Count);
        Assert.True(wake.Session.ResetCount >= 2);
    }

    [Fact]
    public void TurningTheWakeWordOffLeavesTheMicrophoneToARequestThatIsUsingIt()
    {
        var (router, microphone, _, _) = Create();
        router.SetWakeWord(true);
        var listening = router.Listen(new VoiceListenRequest(), _ => { });

        router.SetWakeWord(false);
        Assert.Equal(1, microphone.Subscriptions);

        listening.Dispose();
        Assert.True(microphone.WaitUntilClosed());
    }

    [Fact]
    public void AMicrophoneThatFailsTellsTheRequestThatWasListening()
    {
        var (router, microphone, _, _) = Create();
        var failures = new List<MicrophoneFailure>();
        _ = router.Listen(new VoiceListenRequest(), failures.Add);

        microphone.Fail(MicrophoneFailure.Disconnected);

        Assert.Equal([MicrophoneFailure.Disconnected], failures);
    }

    [Fact]
    public void AMicrophoneThatFailsWhileOnlyTheWakeWordListensIsReportedAndTriedAgainWhenTheWakeWordIsTurnedOnAgain()
    {
        var (router, microphone, _, _) = Create();
        var failures = new List<MicrophoneFailure>();
        router.WakeWordUnavailable += (_, failure) => failures.Add(failure);
        router.SetWakeWord(true);

        microphone.Fail(MicrophoneFailure.NoMicrophone);

        Assert.Equal([MicrophoneFailure.NoMicrophone], failures);
        Assert.False(router.IsListeningForWakeWord);
        router.SetWakeWord(true);
        Assert.Equal(2, microphone.TotalSubscriptions);
        Assert.True(router.IsListeningForWakeWord);
    }

    [Fact]
    public void AWakeWordNobodyAnsweredIsLetGoOfAndTheListenerStartsAgain()
    {
        var (router, microphone, _, wake) = Create();
        router.SetWakeWord(true);
        var heard = new List<WakeHandoff>();
        router.WakeWordHeard += (_, handoff) => heard.Add(handoff);
        var next = 0;
        microphone.Deliver(Block(ref next, 3200));
        wake.Session!.Detect();
        Assert.Single(heard);

        // Nothing was started: after a few seconds the handoff is dropped and a second wake word is heard.
        for (var i = 0; i < 70; i++)
        {
            microphone.Deliver(Block(ref next, 1600));
        }

        wake.Session.Detect();
        Assert.Equal(2, heard.Count);
    }

    [Fact]
    public void AWakeWordWhileAnotherIsWaitingToBeAnsweredIsIgnored()
    {
        var (router, microphone, _, wake) = Create();
        router.SetWakeWord(true);
        var count = 0;
        router.WakeWordHeard += (_, _) => count++;
        var next = 0;
        microphone.Deliver(Block(ref next, 3200));

        wake.Session!.Detect();
        wake.Session.Detect();

        Assert.Equal(1, count);
    }

    [Fact]
    public void TheWakeWordIsTakenOffTheFrontOfWhatWasRecognizedAfterIt()
    {
        var (router, microphone, recognizer, wake) = Create();
        router.SetWakeWord(true);
        WakeHandoff? handoff = null;
        router.WakeWordHeard += (_, heard) => handoff = heard;
        var next = 0;
        microphone.Deliver(Block(ref next, 32000));
        wake.Session!.Detect();
        var listening = router.Listen(new VoiceListenRequest { Handoff = handoff }, _ => { });
        var heardText = new List<SpeechTranscriptEventArgs>();
        listening.Transcribed += (_, e) => heardText.Add(e);

        recognizer.Sessions[0].Raise("kerry what's on my calendar", null);
        recognizer.Sessions[0].Raise("kerry what's on my calendar tomorrow", SpeechEndReason.Endpoint);

        Assert.Equal(["what's on my calendar", "what's on my calendar tomorrow"], heardText.Select(e => e.Text));
        Assert.Equal(SpeechEndReason.Endpoint, heardText[1].Ended);
    }

    [Fact]
    public void ARequestThatDidNotFollowTheWakeWordKeepsEveryWordItHeard()
    {
        var (router, _, recognizer, _) = Create();
        var listening = router.Listen(new VoiceListenRequest(), _ => { });
        var heardText = new List<string>();
        listening.Transcribed += (_, e) => heardText.Add(e.Text);

        recognizer.Sessions[0].Raise("carry the box over here", null);

        Assert.Equal(["carry the box over here"], heardText);
    }

    [Fact]
    public void TheListeningLevelIsTheMicrophonesAndItIsZeroWhenItIsNotListening()
    {
        var (router, microphone, _, _) = Create();
        var listening = router.Listen(new VoiceListenRequest(), _ => { });

        microphone.Level = 0.4;

        Assert.Equal(0.4, listening.Level);
        listening.Dispose();
        Assert.Equal(0, listening.Level);
    }

    [Fact]
    public void AnotherRequestTakesTheMicrophoneFromTheOneBeforeIt()
    {
        var (router, microphone, recognizer, _) = Create();
        var first = router.Listen(new VoiceListenRequest(), _ => { });
        var second = router.Listen(new VoiceListenRequest(), _ => { });

        Assert.True(recognizer.Sessions[0].IsDisposed);
        Assert.False(recognizer.Sessions[1].IsDisposed);
        Assert.True(router.IsListeningToRequest);
        _ = first;
        second.Dispose();
        Assert.True(microphone.WaitUntilClosed());
    }

    // ---- fakes ----

    private sealed class FakeSubscription(FakeMicrophoneSource owner, MicrophoneAudioHandler? handler, Action<MicrophoneFailure> failed) : IMicrophoneAudioSubscription
    {
        public MicrophoneAudioHandler? Handler { get; } = handler;

        public Action<MicrophoneFailure> Failed { get; } = failed;

        public bool IsDisposed { get; private set; }

        public double Level => IsDisposed ? 0 : owner.Level;

        public void Dispose()
        {
            IsDisposed = true;
        }
    }

    private sealed class FakeMicrophoneSource : IMicrophoneAudioSource
    {
        private readonly List<FakeSubscription> _all = [];

        public double Level { get; set; }

        public int Subscriptions => _all.Count(subscription => !subscription.IsDisposed);

        public int TotalSubscriptions => _all.Count;

        public IMicrophoneAudioSubscription Subscribe(MicrophoneAudioHandler? onSamples, Action<MicrophoneFailure> failed)
        {
            var subscription = new FakeSubscription(this, onSamples, failed);
            _all.Add(subscription);
            return subscription;
        }

        public void Deliver(short[] samples)
        {
            foreach (var subscription in _all.Where(subscription => !subscription.IsDisposed).ToList())
            {
                subscription.Handler?.Invoke(samples);
            }
        }

        public void Fail(MicrophoneFailure failure)
        {
            foreach (var subscription in _all.Where(subscription => !subscription.IsDisposed).ToList())
            {
                subscription.Failed(failure);
            }
        }

        // The router disposes an idle subscription on the thread pool.
        public bool WaitUntilClosed() => SpinWait.SpinUntil(() => Subscriptions == 0, TimeSpan.FromSeconds(5));
    }

    private sealed class FakeRecognizerSession(SpeechRecognitionOptions? options) : ISpeechRecognitionSession
    {
        public SpeechRecognitionOptions? Options { get; } = options;

        public List<short> Pushed { get; } = [];

        public bool IsDisposed { get; private set; }

        public bool IsEnded => IsDisposed;

        public event EventHandler<SpeechTranscriptEventArgs>? Transcribed;

        public void Push(ReadOnlySpan<short> samples) => Pushed.AddRange(samples.ToArray());

        public void Finish()
        {
        }

        public void Raise(string text, SpeechEndReason? ended) => Transcribed?.Invoke(this, new SpeechTranscriptEventArgs(text, ended));

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeRecognizer : ISpeechToTextService
    {
        public List<FakeRecognizerSession> Sessions { get; } = [];

        public VoiceEngineStatus Status => new(VoiceEngineState.Ready);

        public event EventHandler? StatusChanged { add { } remove { } }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public ISpeechRecognitionSession StartSession(SpeechRecognitionOptions? options = null)
        {
            var session = new FakeRecognizerSession(options);
            Sessions.Add(session);
            return session;
        }

        public void Unload()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeWakeSession : IWakeWordSession
    {
        public List<short> Pushed { get; } = [];

        public int ResetCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public event EventHandler<WakeWordDetectedEventArgs>? Detected;

        public void Push(ReadOnlySpan<short> samples) => Pushed.AddRange(samples.ToArray());

        public void Reset() => ResetCount++;

        public void Detect() => Detected?.Invoke(this, new WakeWordDetectedEventArgs(TimeSpan.FromSeconds(1)));

        public void Dispose() => IsDisposed = true;
    }

    private sealed class FakeWakeWord : IWakeWordService
    {
        public FakeWakeSession? Session { get; private set; }

        public VoiceEngineStatus Status => new(VoiceEngineState.Ready);

        public event EventHandler? StatusChanged { add { } remove { } }

        public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public IWakeWordSession StartSession() => Session = new FakeWakeSession();

        public void Unload()
        {
        }

        public void Dispose()
        {
        }
    }
}
