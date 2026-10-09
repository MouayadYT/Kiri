namespace Assistant.Windows.Audio;

/// <summary>Reduces audio samples to a loudness level.</summary>
internal static class AudioLevel
{
    /// <summary>
    /// The root-mean-square amplitude of 16-bit samples, from 0 (silence, or no samples) to 1 (a full-scale square
    /// wave).
    /// </summary>
    public static double Rms(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return 0;
        }

        double sumOfSquares = 0;
        foreach (var sample in samples)
        {
            sumOfSquares += (double)sample * sample;
        }

        return Math.Min(1, Math.Sqrt(sumOfSquares / samples.Length) / 32768);
    }
}
