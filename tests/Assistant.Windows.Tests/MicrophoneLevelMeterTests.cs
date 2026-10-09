using System.Collections.Concurrent;
using Assistant.Windows.Audio;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Assistant.Windows.Tests;

public sealed class MicrophoneLevelMeterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void LevelIsTheRootMeanSquareOfTheSamples()
    {
        Assert.Equal(0, AudioLevel.Rms([]));
        Assert.Equal(0, AudioLevel.Rms(new short[160]));
        Assert.Equal(1, AudioLevel.Rms([short.MinValue, short.MinValue, short.MinValue]));
        Assert.Equal(16384 / 32768.0, AudioLevel.Rms([16384, -16384, 16384, -16384]), 9);

        // A sine wave's RMS is its amplitude over the square root of two.
        var sine = Enumerable.Range(0, 1600).Select(i => (short)(16384 * Math.Sin(2 * Math.PI * i / 32))).ToArray();
        Assert.Equal(0.5 / Math.Sqrt(2), AudioLevel.Rms(sine), 3);
    }

    [Fact]
    public void SessionReportsTheLatestLevelAndClosesTheMicrophoneWhenDisposed()
    {
        var capture = new FakeCapture();
        var failures = new ConcurrentQueue<MicrophoneFailure>();
        var session = new MicrophoneLevelMeter(new TestLogger(), () => capture).Start(failures.Enqueue);

        Assert.True(capture.Opened.Wait(Timeout), "The capture did not start.");
        Assert.NotEqual(Environment.CurrentManagedThreadId, capture.ThreadId);
        capture.Deliver([16384, -16384]);
        Assert.Equal(0.5, session.Level, 6);
        capture.Deliver([]);
        Assert.Equal(0, session.Level);
        capture.Deliver([8192, -8192]);
        Assert.Equal(0.25, session.Level, 6);

        session.Dispose();
        Assert.True(capture.Closed.Wait(Timeout), "Disposing did not close the microphone.");
        WaitUntil(() => session.Level == 0);
        Assert.Empty(failures);
        session.Dispose();
    }

    [Fact]
    public void StartingDoesNotWaitForTheDeviceToOpen()
    {
        var capture = new FakeCapture { Gate = new ManualResetEventSlim() };
        using var session = new MicrophoneLevelMeter(new TestLogger(), () => capture).Start(_ => { });

        Assert.Equal(0, session.Level);
        Assert.False(capture.Opened.IsSet);
        capture.Gate.Set();
        Assert.True(capture.Opened.Wait(Timeout));
    }

    [Theory]
    [InlineData(MicrophoneFailure.NoMicrophone)]
    [InlineData(MicrophoneFailure.AccessDenied)]
    [InlineData(MicrophoneFailure.Disconnected)]
    [InlineData(MicrophoneFailure.Unavailable)]
    public void FailuresAreReportedOnceAndLoggedWithoutAudio(MicrophoneFailure failure)
    {
        var capture = new FakeCapture { Failure = new MicrophoneException(failure, unchecked((int)0x80070005)) };
        var logger = new TestLogger();
        var failures = new ConcurrentQueue<MicrophoneFailure>();
        using var session = new MicrophoneLevelMeter(logger, () => capture).Start(failures.Enqueue);

        WaitUntil(() => !failures.IsEmpty);
        Thread.Sleep(50);
        Assert.Equal(new[] { failure }, failures.ToArray());
        Assert.Equal(0, session.Level);
        Assert.Contains(logger.Messages, line => line.Contains(failure.ToString()) && line.Contains("0x80070005"));
        Assert.DoesNotContain(logger.Messages, line => line.Contains("0.5"));
    }

    [Fact]
    public void UnexpectedErrorsAndCapturesThatEndByThemselvesAreFailures()
    {
        var failures = new ConcurrentQueue<MicrophoneFailure>();
        using var crashed = new MicrophoneLevelMeter(new TestLogger(), () => new FakeCapture { Failure = new InvalidOperationException() })
            .Start(failures.Enqueue);
        using var ended = new MicrophoneLevelMeter(new TestLogger(), () => new FakeCapture { EndsByItself = true })
            .Start(failures.Enqueue);

        WaitUntil(() => failures.Count == 2);
        Assert.All(failures, failure => Assert.Equal(MicrophoneFailure.Unavailable, failure));
    }

    [Fact]
    public void NoFailureIsReportedAfterTheSessionIsDisposed()
    {
        var capture = new FakeCapture { Gate = new ManualResetEventSlim(), Failure = new MicrophoneException(MicrophoneFailure.Disconnected, -1) };
        var failures = new ConcurrentQueue<MicrophoneFailure>();
        var session = new MicrophoneLevelMeter(new TestLogger(), () => capture).Start(failures.Enqueue);

        session.Dispose();
        capture.Gate.Set();
        Assert.True(capture.Finished.Wait(Timeout));
        Thread.Sleep(50);
        Assert.Empty(failures);
    }

    // Opens the real default microphone for a moment, only when ASSISTANT_TEST_MICROPHONE=1. Levels only; no audio is
    // kept. Without a microphone, or with microphone access off, it must fail with a known reason rather than crash.
    [Fact]
    public void RealMicrophoneDeliversSamplesOrAKnownFailure()
    {
        if (Environment.GetEnvironmentVariable("ASSISTANT_TEST_MICROPHONE") != "1")
        {
            return;
        }

        using var stop = new ManualResetEvent(false);
        var blocks = 0;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { new WasapiMicrophoneCapture().Run(samples => { if (Interlocked.Increment(ref blocks) >= 20) stop.Set(); }, stop); }
            catch (Exception exception) { error = exception; }
        });
        thread.Start();
        stop.WaitOne(Timeout);
        stop.Set();
        Assert.True(thread.Join(Timeout), "The microphone did not close.");
        if (error is not null)
        {
            Assert.IsType<MicrophoneException>(error);
        }
        else
        {
            Assert.True(blocks >= 20, $"Only {blocks} blocks of samples arrived.");
        }
    }

    private static void WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(5);
        Assert.True(condition());
    }

    // Stands in for the device: the test hands it blocks of samples, which reach the session's handler synchronously.
    private sealed class FakeCapture : IMicrophoneCapture
    {
        private readonly SemaphoreSlim _delivered = new(0);
        private readonly ConcurrentQueue<short[]> _blocks = new();

        public ManualResetEventSlim? Gate { get; init; }
        public Exception? Failure { get; init; }
        public bool EndsByItself { get; init; }
        public ManualResetEventSlim Opened { get; } = new();
        public ManualResetEventSlim Closed { get; } = new();
        public ManualResetEventSlim Finished { get; } = new();
        public int ThreadId { get; private set; }
        private AutoResetEvent Handled { get; } = new(false);

        public void Run(MicrophoneSamplesHandler onSamples, WaitHandle stop)
        {
            try
            {
                Gate?.Wait();
                ThreadId = Environment.CurrentManagedThreadId;
                if (Failure is not null) throw Failure;
                Opened.Set();
                if (EndsByItself) return;
                var handles = new[] { stop, _delivered.AvailableWaitHandle };
                while (WaitHandle.WaitAny(handles) != 0)
                {
                    _delivered.Wait();
                    if (_blocks.TryDequeue(out var block)) onSamples(block);
                    Handled.Set();
                }

                Closed.Set();
            }
            finally
            {
                Finished.Set();
            }
        }

        public void Deliver(short[] block)
        {
            _blocks.Enqueue(block);
            _delivered.Release();
            Assert.True(Handled.WaitOne(Timeout), "The block was not handled.");
        }
    }

    private sealed class TestLogger : ILogger<MicrophoneLevelMeter>
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }
}
