using Assistant.Core.Voice;

namespace Assistant.Windows.Audio;

/// <summary>The sound device a <see cref="RenderStream"/> plays on, as the stream needs it: a buffer that is filled in steps and an event that says when it has room.</summary>
internal interface IRenderDevice : IDisposable
{
    /// <summary>How many frames the device's buffer holds.</summary>
    int BufferFrames { get; }

    /// <summary>Set by the device whenever it has played enough of its buffer to take more.</summary>
    WaitHandle Ready { get; }

    /// <summary>How many frames are in the device's buffer and have not been played yet.</summary>
    int Padding { get; }

    /// <summary>Puts frames in the device's buffer. At most <see cref="BufferFrames"/> minus <see cref="Padding"/>.</summary>
    void Write(ReadOnlySpan<short> frames);

    /// <summary>Starts the device playing what its buffer holds.</summary>
    void Start();

    /// <summary>Stops the device. What is left in its buffer stays there.</summary>
    void Stop();

    /// <summary>Empties the device's buffer. Only while it is stopped.</summary>
    void Reset();
}

/// <summary>
/// A stream of speech on the speakers (PROJECT_SPEC §4.2, step 125): the voice's samples are queued here and a thread of the stream's own feeds them to the
/// sound device a little at a time, so the voice never waits for the speakers and the speakers never wait for the voice. The device is started with the
/// first samples and stopped when everything has been played, so a stream that is not speaking costs nothing; <see cref="Clear"/> silences it at once.
/// </summary>
internal sealed class RenderStream : IAudioOutputStream
{
    private readonly Func<IRenderDevice> _createDevice;
    private readonly int _sampleRate;
    private readonly ManualResetEventSlim _opened = new(false);
    private readonly object _lock = new();
    private readonly Queue<short[]> _queue = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _clearDone = new(false);
    private readonly List<TaskCompletionSource> _drainWaiters = [];
    private readonly Thread _thread;
    private int _headOffset;
    private long _queuedFrames;
    private long _inFlight;
    private IRenderDevice _device = null!;
    private Exception? _openFailure;
    private volatile bool _disposed;
    private volatile bool _failed;
    private volatile int _lastPadding;
    private bool _clearRequested;
    private bool _started;

    /// <summary>
    /// Opens a stream at <paramref name="sampleRate"/> hertz. The device is made, used and let go of on the stream's own thread (a sound device wants a
    /// thread that has COM, for as long as it is used), and this returns once it is made.
    /// </summary>
    /// <exception cref="InvalidOperationException">The device could not be opened.</exception>
    public RenderStream(int sampleRate, Func<IRenderDevice> createDevice)
    {
        _sampleRate = sampleRate;
        _createDevice = createDevice;
        _thread = new Thread(Run) { IsBackground = true, Name = "Speech playback", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        if (!_opened.Wait(TimeSpan.FromSeconds(5)))
        {
            _disposed = true;
            throw new InvalidOperationException("The speakers did not open.");
        }

        if (_openFailure is not null)
        {
            throw new InvalidOperationException("The speakers could not be opened.", _openFailure);
        }
    }

    /// <inheritdoc/>
    public TimeSpan Queued
    {
        get
        {
            long frames;
            lock (_lock)
            {
                frames = _queuedFrames + _inFlight;
            }

            return TimeSpan.FromSeconds((frames + _lastPadding) / (double)_sampleRate);
        }
    }

    /// <inheritdoc/>
    public void Write(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty || _disposed || _failed)
        {
            return;
        }

        var frames = new short[samples.Length];
        for (var i = 0; i < samples.Length; i++)
        {
            frames[i] = (short)Math.Clamp((int)Math.Round(samples[i] * 32767f), short.MinValue, short.MaxValue);
        }

        lock (_lock)
        {
            _queue.Enqueue(frames);
            _queuedFrames += frames.Length;
        }

        _wake.Set();
    }

    /// <inheritdoc/>
    public void Clear()
    {
        if (_disposed || _failed)
        {
            return;
        }

        lock (_lock)
        {
            _queue.Clear();
            _queuedFrames = 0;
            _headOffset = 0;
            _clearRequested = true;
        }

        _wake.Set();

        // The device is silenced on its thread; this returns once it has been, so that a stop is a stop when it returns.
        if (Thread.CurrentThread != _thread)
        {
            _clearDone.WaitOne(TimeSpan.FromMilliseconds(500));
        }
    }

    /// <inheritdoc/>
    public Task WaitUntilDrainedAsync(CancellationToken cancellationToken)
    {
        TaskCompletionSource waiter;
        lock (_lock)
        {
            if (_disposed || _failed || (_queuedFrames == 0 && _inFlight == 0 && !_started))
            {
                return Task.CompletedTask;
            }

            waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _drainWaiters.Add(waiter);
        }

        return waiter.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _wake.Set();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        CompleteWaiters();
        _wake.Dispose();
        _clearDone.Dispose();
        _opened.Dispose();
    }

    private void Run()
    {
        try
        {
            _device = _createDevice();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _openFailure = exception;
            _opened.Set();
            return;
        }

        _opened.Set();
        try
        {
            WaitHandle[] handles = [_wake, _device.Ready];
            while (!_disposed)
            {
                // Idle: nothing queued, and the device stopped. Wake for the first samples.
                WaitHandle.WaitAny(handles, _started ? 100 : Timeout.Infinite);
                if (_disposed)
                {
                    break;
                }

                bool clear;
                lock (_lock)
                {
                    clear = _clearRequested;
                    _clearRequested = false;
                }

                if (clear)
                {
                    if (_started)
                    {
                        _device.Stop();
                        _device.Reset();
                        _started = false;
                    }

                    _lastPadding = 0;
                    CompleteWaiters();
                    _clearDone.Set();
                    continue;
                }

                Fill();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The device went away while it played (headphones unplugged, the output changed): what was queued is dropped and the stream ends
            // quietly, so waiters are released and a later write is ignored, rather than an exception on this thread taking the app down.
            _failed = true;
            lock (_lock)
            {
                _queue.Clear();
                _queuedFrames = 0;
                _inFlight = 0;
                _headOffset = 0;
            }

            CompleteWaiters();
            _clearDone.Set();
        }
        finally
        {
            try
            {
                if (_started)
                {
                    _device.Stop();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // The device is going away anyway.
            }

            _device.Dispose();
        }
    }

    // Moves what is queued into the device's buffer as far as it has room, starts the device the first time, and ends the stream's turn when everything
    // has been played.
    private void Fill()
    {
        short[]? taken = null;
        var padding = _device.Padding;
        _lastPadding = padding;
        var room = Math.Max(0, _device.BufferFrames - padding);
        if (room > 0)
        {
            taken = Take(room);
        }

        if (taken is { Length: > 0 })
        {
            _device.Write(taken);
            _lastPadding = _device.Padding;
            if (!_started)
            {
                _device.Start();
                _started = true;
            }
        }

        // What was taken is in the device now (and the device started), so a waiter is told the truth from here on.
        lock (_lock)
        {
            _inFlight = 0;
        }

        if (_started)
        {
            bool empty;
            lock (_lock)
            {
                empty = _queuedFrames == 0;
            }

            _lastPadding = _device.Padding;
            if (empty && _lastPadding == 0)
            {
                // Everything was heard.
                _device.Stop();
                _started = false;
                CompleteWaiters();
            }
        }
    }

    // Up to <count> frames from the front of the queue.
    private short[]? Take(int count)
    {
        lock (_lock)
        {
            if (_queue.Count == 0)
            {
                return null;
            }

            var result = new short[(int)Math.Min(count, _queuedFrames)];
            var written = 0;
            while (written < result.Length && _queue.Count > 0)
            {
                var head = _queue.Peek();
                var available = head.Length - _headOffset;
                var take = Math.Min(available, result.Length - written);
                Array.Copy(head, _headOffset, result, written, take);
                written += take;
                _headOffset += take;
                if (_headOffset >= head.Length)
                {
                    _queue.Dequeue();
                    _headOffset = 0;
                }
            }

            _queuedFrames -= written;
            _inFlight = written;
            return result;
        }
    }

    private void CompleteWaiters()
    {
        TaskCompletionSource[] waiters;
        lock (_lock)
        {
            waiters = [.. _drainWaiters];
            _drainWaiters.Clear();
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }
}
