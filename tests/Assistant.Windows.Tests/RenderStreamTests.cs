using Assistant.Core.Voice;
using Assistant.Windows.Audio;
using Xunit;

namespace Assistant.Windows.Tests;

public sealed class RenderStreamTests
{
    private const int Rate = 22_050;

    private static bool Eventually(Func<bool> condition, int milliseconds = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    private static float[] Ramp(int count) => Enumerable.Range(0, count).Select(i => (i % 1000) / 1000f).ToArray();

    [Fact]
    public async Task WhatIsWrittenIsPlayedInOrderAndTheStreamSaysWhenItIsDone()
    {
        using var device = new FakeDevice(Rate);
        using var stream = new RenderStream(Rate, () => device);
        var first = Ramp(3000);
        var second = Ramp(5000);

        stream.Write(first);
        stream.Write(second);
        await stream.WaitUntilDrainedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(8000, device.Played.Count);
        Assert.Equal((short)Math.Round(first[10] * 32767f), device.Played[10]);
        Assert.Equal((short)Math.Round(second[10] * 32767f), device.Played[3010]);
        Assert.True(Eventually(() => !device.Started));
        Assert.Equal(TimeSpan.Zero, stream.Queued);
    }

    [Fact]
    public async Task ASilentStreamIsStoppedAndWritingStartsItAgain()
    {
        using var device = new FakeDevice(Rate);
        using var stream = new RenderStream(Rate, () => device);
        stream.Write(Ramp(500));
        await stream.WaitUntilDrainedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(Eventually(() => !device.Started));

        stream.Write(Ramp(500));
        await stream.WaitUntilDrainedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(1000, device.Played.Count);
        Assert.Equal(2, device.Starts);
    }

    [Fact]
    public async Task ClearSilencesTheSpeakersAtOnceAndDropsWhatWasQueued()
    {
        using var device = new FakeDevice(Rate);
        using var stream = new RenderStream(Rate, () => device);
        stream.Write(Ramp(Rate * 5));
        Assert.True(Eventually(() => device.Played.Count > 0));

        stream.Clear();
        var playedAtClear = device.Played.Count;
        Thread.Sleep(100);

        Assert.Equal(playedAtClear, device.Played.Count);
        Assert.False(device.Started);
        Assert.Equal(1, device.Resets);
        Assert.Equal(TimeSpan.Zero, stream.Queued);
        Assert.True(stream.WaitUntilDrainedAsync(CancellationToken.None).IsCompleted);

        // And it speaks again afterwards.
        stream.Write(Ramp(400));
        await stream.WaitUntilDrainedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(playedAtClear + 400, device.Played.Count);
    }

    [Fact]
    public async Task ClearEndsAWaitForTheSoundToBeHeard()
    {
        using var device = new FakeDevice(Rate);
        using var stream = new RenderStream(Rate, () => device);
        stream.Write(Ramp(Rate * 5));
        var waiting = stream.WaitUntilDrainedAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        stream.Clear();

        await waiting.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TheQueuedTimeCountsWhatIsWaitingAndWhatTheDeviceHolds()
    {
        using var device = new FakeDevice(Rate) { Paused = true };
        using var stream = new RenderStream(Rate, () => device);

        stream.Write(Ramp(Rate * 2));

        Assert.True(Eventually(() => stream.Queued >= TimeSpan.FromSeconds(1.9)));
        Assert.True(stream.Queued <= TimeSpan.FromSeconds(2.01));
    }

    [Fact]
    public void ADeviceThatCannotBeOpenedFailsTheOpen()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => new RenderStream(Rate, () => throw new InvalidOperationException("No speakers.")));

        Assert.Contains("could not be opened", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DisposingLetsGoOfTheDeviceOnItsOwnThread()
    {
        var device = new FakeDevice(Rate);
        var stream = new RenderStream(Rate, () => device);
        stream.Write(Ramp(Rate));
        Assert.True(Eventually(() => device.Started));

        stream.Dispose();

        Assert.True(device.IsDisposed);
        Assert.False(device.Started);
        stream.Write(Ramp(100));
        stream.Dispose();
    }

    [Fact]
    public async Task SamplesOutsideTheRangeAreClippedNotWrapped()
    {
        using var device = new FakeDevice(Rate);
        using var stream = new RenderStream(Rate, () => device);

        stream.Write([2f, -2f, 0f]);
        await stream.WaitUntilDrainedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal([short.MaxValue, short.MinValue, 0], device.Played.Take(3).ToArray());
    }

    [Fact]
    public async Task AWaiterIsNotToldItIsDoneWhileWhatWasTakenIsStillOnItsWayToTheDevice()
    {
        using var device = new FakeDevice(Rate) { BeforeWrite = () => Thread.Sleep(300) };
        using var stream = new RenderStream(Rate, () => device);

        stream.Write(Ramp(100));
        Thread.Sleep(100);
        var drained = stream.WaitUntilDrainedAsync(CancellationToken.None);

        Assert.False(drained.IsCompleted);
        await drained.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(100, device.Played.Count);
    }

    [Fact]
    public async Task ADeviceThatFailsWhilePlayingEndsTheStreamQuietlyAndReleasesWhoWaits()
    {
        using var device = new FakeDevice(Rate) { FailOnWrite = true };
        using var stream = new RenderStream(Rate, () => device);

        stream.Write(Ramp(100));
        await stream.WaitUntilDrainedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        // Later writes and stops are ignored, not thrown, and the device is let go of.
        stream.Write(Ramp(100));
        stream.Clear();
        Assert.Equal(TimeSpan.Zero, stream.Queued);
        Assert.True(Eventually(() => device.IsDisposed));
        Assert.True(stream.WaitUntilDrainedAsync(CancellationToken.None).IsCompleted);
    }

    // Opens the real default speakers for a moment, only when ASSISTANT_TEST_SPEAKERS=1: a very quiet tone is played to the end, and a longer one is cleared and must
    // stop at once. Without speakers it must fail with a known reason rather than crash.
    [Fact]
    public async Task RealSpeakersPlayAndAreSilencedAtOnceOrFailWithAKnownReason()
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_TEST_SPEAKERS") != "1")
        {
            return;
        }

        IAudioOutputStream stream;
        try
        {
            stream = new WasapiAudioOutput().Open(24_000);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using (stream)
        {
            var tone = Enumerable.Range(0, 24_000).Select(i => (float)(0.02 * Math.Sin(2 * Math.PI * 440 * i / 24_000))).ToArray();
            stream.Write(tone.AsSpan(0, 9_600));
            await stream.WaitUntilDrainedAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(TimeSpan.Zero, stream.Queued);

            stream.Write(tone);
            stream.Write(tone);
            await Task.Delay(100);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            stream.Clear();
            Assert.True(timer.ElapsedMilliseconds < 300, $"Clearing took {timer.ElapsedMilliseconds} ms.");
            Assert.Equal(TimeSpan.Zero, stream.Queued);
        }
    }

    // A sound device that plays what it is given in real time, on a thread of its own, and says when its buffer has room.
    private sealed class FakeDevice : IRenderDevice
    {
        private readonly object _lock = new();
        private readonly AutoResetEvent _ready = new(false);
        private readonly Thread _thread;
        private readonly int _rate;
        private readonly List<short> _buffered = [];
        private readonly List<short> _played = [];
        private volatile bool _stop;

        public FakeDevice(int rate)
        {
            _rate = rate;
            _thread = new Thread(Consume) { IsBackground = true };
            _thread.Start();
        }

        public int BufferFrames => 4000;

        public WaitHandle Ready => _ready;

        public bool Paused { get; set; }

        // Runs before each write, to hold the stream's thread with frames taken from its queue and not yet in the device.
        public Action? BeforeWrite { get; set; }

        // Makes a write fail the way a device that was unplugged does.
        public bool FailOnWrite { get; set; }

        public bool Started { get; private set; }

        public int Starts { get; private set; }

        public int Resets { get; private set; }

        public bool IsDisposed { get; private set; }

        public IReadOnlyList<short> Played
        {
            get
            {
                lock (_lock)
                {
                    return [.. _played];
                }
            }
        }

        public int Padding
        {
            get
            {
                lock (_lock)
                {
                    return _buffered.Count;
                }
            }
        }

        public void Write(ReadOnlySpan<short> frames)
        {
            BeforeWrite?.Invoke();
            if (FailOnWrite)
            {
                throw new InvalidOperationException("The device went away.");
            }

            lock (_lock)
            {
                Assert.True(frames.Length <= BufferFrames - _buffered.Count, "The device was given more than it had room for.");
                _buffered.AddRange(frames.ToArray());
            }
        }

        public void Start()
        {
            lock (_lock)
            {
                Started = true;
                Starts++;
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                Started = false;
            }
        }

        public void Reset()
        {
            lock (_lock)
            {
                Assert.False(Started, "The device was reset while it was playing.");
                _buffered.Clear();
                Resets++;
            }
        }

        public void Dispose()
        {
            IsDisposed = true;
            _stop = true;
            _thread.Join(2000);
        }

        private void Consume()
        {
            while (!_stop)
            {
                Thread.Sleep(10);
                lock (_lock)
                {
                    if (Started && !Paused)
                    {
                        var count = Math.Min(_buffered.Count, _rate / 100);
                        _played.AddRange(_buffered.Take(count));
                        _buffered.RemoveRange(0, count);
                    }
                }

                _ready.Set();
            }
        }
    }
}

public sealed class SharedMicrophoneTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void SubscribersShareOneCaptureAndTheDeviceClosesWithTheLastOfThem()
    {
        var captures = new List<FakeCapture>();
        var meter = new MicrophoneLevelMeter(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MicrophoneLevelMeter>.Instance,
            () =>
            {
                var capture = new FakeCapture();
                captures.Add(capture);
                return capture;
            });
        var heardByFirst = new List<short>();
        var heardBySecond = new List<short>();
        var first = meter.Subscribe(samples => heardByFirst.AddRange(samples.ToArray()), _ => { });
        var second = meter.Subscribe(samples => heardBySecond.AddRange(samples.ToArray()), _ => { });
        var level = meter.Start(_ => { });

        Assert.Single(captures);
        Assert.True(captures[0].Opened.Wait(Timeout));
        captures[0].Deliver([16384, -16384]);

        Assert.Equal([16384, -16384], heardByFirst);
        Assert.Equal([16384, -16384], heardBySecond);
        Assert.Equal(0.5, first.Level, 6);
        Assert.Equal(0.5, level.Level, 6);

        first.Dispose();
        second.Dispose();
        Assert.False(captures[0].Closed.IsSet);
        level.Dispose();
        Assert.True(captures[0].Closed.Wait(Timeout), "The microphone stayed open after the last subscription.");
        Assert.Equal(0, first.Level);
    }

    [Fact]
    public void ASubscriptionAfterTheDeviceClosedOpensItAgain()
    {
        var captures = new List<FakeCapture>();
        var meter = new MicrophoneLevelMeter(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<MicrophoneLevelMeter>.Instance,
            () =>
            {
                var capture = new FakeCapture();
                captures.Add(capture);
                return capture;
            });

        meter.Subscribe(null, _ => { }).Dispose();
        var again = meter.Subscribe(null, _ => { });

        Assert.Equal(2, captures.Count);
        Assert.True(captures[1].Opened.Wait(Timeout));
        again.Dispose();
    }

    [Fact]
    public void AFailureIsToldToEverySubscriberOnce()
    {
        var capture = new FakeCapture { Failure = new MicrophoneException(MicrophoneFailure.Disconnected, -1) };
        var meter = new MicrophoneLevelMeter(Microsoft.Extensions.Logging.Abstractions.NullLogger<MicrophoneLevelMeter>.Instance, () => capture);
        var told = new List<MicrophoneFailure>();
        var gate = new object();
        using var a = meter.Subscribe(null, failure => { lock (gate) { told.Add(failure); } });
        using var b = meter.Subscribe(null, failure => { lock (gate) { told.Add(failure); } });

        Assert.True(SpinWait.SpinUntil(() => { lock (gate) { return told.Count == 2; } }, Timeout));
        Thread.Sleep(50);

        lock (gate)
        {
            Assert.Equal([MicrophoneFailure.Disconnected, MicrophoneFailure.Disconnected], told);
        }
    }

    // A microphone that cannot be opened fails before its first subscriber has finished subscribing; the subscriber must still be told, every time.
    [Fact]
    public void ADeviceThatFailsAtOnceStillTellsItsFirstSubscriber()
    {
        for (var attempt = 0; attempt < 300; attempt++)
        {
            var meter = new MicrophoneLevelMeter(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<MicrophoneLevelMeter>.Instance,
                () => new FakeCapture { Failure = new MicrophoneException(MicrophoneFailure.NoMicrophone, -1) });
            using var told = new ManualResetEventSlim();
            using var subscription = meter.Subscribe(null, _ => told.Set());

            Assert.True(told.Wait(Timeout), $"The subscriber was never told (attempt {attempt}).");
        }
    }

    private sealed class FakeCapture : IMicrophoneCapture
    {
        private readonly SemaphoreSlim _delivered = new(0);
        private readonly Queue<short[]> _blocks = new();
        private readonly AutoResetEvent _handled = new(false);

        public Exception? Failure { get; init; }

        public ManualResetEventSlim Opened { get; } = new();

        public ManualResetEventSlim Closed { get; } = new();

        public void Run(MicrophoneSamplesHandler onSamples, WaitHandle stop)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Opened.Set();
            var handles = new[] { stop, _delivered.AvailableWaitHandle };
            while (WaitHandle.WaitAny(handles) != 0)
            {
                _delivered.Wait();
                short[]? block;
                lock (_blocks)
                {
                    block = _blocks.Dequeue();
                }

                onSamples(block);
                _handled.Set();
            }

            Closed.Set();
        }

        public void Deliver(short[] block)
        {
            lock (_blocks)
            {
                _blocks.Enqueue(block);
            }

            _delivered.Release();
            Assert.True(_handled.WaitOne(Timeout), "The block was not handled.");
        }
    }
}
