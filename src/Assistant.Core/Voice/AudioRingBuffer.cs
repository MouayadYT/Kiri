namespace Assistant.Core.Voice;

/// <summary>
/// The last few seconds of the microphone's audio, in memory, kept only so that a request that began a moment before the wake word was recognized does not
/// lose its first words (PROJECT_SPEC §4.2, step 125). It holds a fixed number of samples, overwrites the oldest, and is never written anywhere. Not
/// thread-safe: the owner locks.
/// </summary>
public sealed class AudioRingBuffer
{
    private readonly short[] _samples;
    private int _next;
    private long _total;

    /// <summary>Creates a buffer that holds the last <paramref name="capacity"/> samples.</summary>
    public AudioRingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _samples = new short[capacity];
    }

    /// <summary>How many samples it holds at most.</summary>
    public int Capacity => _samples.Length;

    /// <summary>How many samples it holds now.</summary>
    public int Count => (int)Math.Min(_total, _samples.Length);

    /// <summary>How many samples have been written since it was created or last cleared, including those that were overwritten.</summary>
    public long TotalWritten => _total;

    /// <summary>Adds <paramref name="samples"/> after what is held, forgetting the oldest if it is full.</summary>
    public void Write(ReadOnlySpan<short> samples)
    {
        if (samples.Length >= _samples.Length)
        {
            samples[^_samples.Length..].CopyTo(_samples);
            _next = 0;
            _total += samples.Length;
            return;
        }

        var first = Math.Min(samples.Length, _samples.Length - _next);
        samples[..first].CopyTo(_samples.AsSpan(_next));
        samples[first..].CopyTo(_samples);
        _next = (_next + samples.Length) % _samples.Length;
        _total += samples.Length;
    }

    /// <summary>A copy of the last <paramref name="count"/> samples written (fewer when it holds fewer), oldest first.</summary>
    public short[] Last(int count)
    {
        count = Math.Clamp(count, 0, Count);
        var copy = new short[count];
        var start = (_next - count + _samples.Length) % _samples.Length;
        var first = Math.Min(count, _samples.Length - start);
        _samples.AsSpan(start, first).CopyTo(copy);
        _samples.AsSpan(0, count - first).CopyTo(copy.AsSpan(first));
        return copy;
    }

    /// <summary>Forgets everything.</summary>
    public void Clear()
    {
        Array.Clear(_samples);
        _next = 0;
        _total = 0;
    }
}
