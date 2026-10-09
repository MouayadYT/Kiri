using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Assistant.Windows.Audio;

/// <summary>What a microphone was heard to do while it was tried.</summary>
/// <param name="Peak">The loudest level heard, from 0 (silence) to 1 (full scale).</param>
/// <param name="Heard">Whether someone was heard: the level was up for more than a click.</param>
/// <param name="Failure">Why it could not be listened to, or <see langword="null"/> when it could.</param>
public sealed record MicrophoneProbeResult(double Peak, bool Heard, MicrophoneFailure? Failure)
{
    /// <summary>
    /// The level from which a microphone counts as hearing someone speak: about a quiet voice an arm's length away. A room's own hum stays well under
    /// it, and a device that is not a microphone at all (a virtual one that nothing feeds) stays at nothing.
    /// </summary>
    public const double HeardLevel = 0.008;
}

/// <summary>
/// Tries one microphone for a moment, so that the user can tell which of several is the one that hears them: a PC may list a headset that is switched
/// off, a camera's microphone and the virtual ones that streaming and audio software add, and the one Windows uses by default may be any of them.
/// </summary>
public interface IMicrophoneProbe
{
    /// <summary>
    /// Listens to the microphone <paramref name="deviceId"/> (<see langword="null"/> for the one Windows uses) for up to <paramref name="duration"/>, and
    /// stops as soon as someone is heard. Each level is given to <paramref name="level"/> as it is read, on the caller's context. Nothing is recorded.
    /// </summary>
    Task<MicrophoneProbeResult> ListenAsync(string? deviceId, TimeSpan duration, Action<double>? level = null, CancellationToken cancellationToken = default);
}

/// <summary>The real thing: the device is opened as the Assistant's own listening opens it, and closed again.</summary>
public sealed class MicrophoneProbe(ILogger<MicrophoneLevelMeter>? logger = null) : IMicrophoneProbe
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(40);

    // Someone is heard once the level has been up for a few readings: a click of the mouse is not a voice.
    private const int HeardReadings = 3;

    /// <inheritdoc/>
    public async Task<MicrophoneProbeResult> ListenAsync(string? deviceId, TimeSpan duration, Action<double>? level = null, CancellationToken cancellationToken = default)
    {
        var meter = new MicrophoneLevelMeter(logger ?? NullLogger<MicrophoneLevelMeter>.Instance, new MicrophoneChoice { DeviceId = deviceId });

        // Written on the capture's thread, read on the caller's; -1 while nothing has failed.
        var failed = -1;
        using var session = meter.Start(reason => Volatile.Write(ref failed, (int)reason));
        var peak = 0.0;
        var heard = 0;
        var until = Environment.TickCount64 + (long)duration.TotalMilliseconds;
        while (Environment.TickCount64 < until && Volatile.Read(ref failed) < 0 && heard < HeardReadings)
        {
            await Task.Delay(Step, cancellationToken).ConfigureAwait(true);
            var now = session.Level;
            peak = Math.Max(peak, now);
            if (now >= MicrophoneProbeResult.HeardLevel)
            {
                heard++;
            }

            level?.Invoke(now);
        }

        var failure = Volatile.Read(ref failed);
        return new MicrophoneProbeResult(peak, failure < 0 && heard >= HeardReadings, failure < 0 ? null : (MicrophoneFailure)failure);
    }
}
