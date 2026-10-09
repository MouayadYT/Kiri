namespace Assistant.Windows.Clipboard;

/// <summary>
/// Tells the clipboard history's watcher that a copy is the Assistant's own doing and is not something the user chose to copy: the copy
/// fallback for reading a selection presses Copy in another application and puts the user's clipboard back (PROJECT_SPEC §4.5). While
/// it is going on, and for a moment after, the watcher ignores what it sees, so that text the user only selected never enters the
/// history.
/// </summary>
internal static class ClipboardSuppression
{
    private const long TailMilliseconds = 2000;
    private static int _active;
    private static long _quietAt;

    /// <summary>Whether a copy of the Assistant's own is going on, or ended a moment ago.</summary>
    public static bool IsSuppressed => Volatile.Read(ref _active) > 0 || Environment.TickCount64 < Volatile.Read(ref _quietAt);

    /// <summary>Starts a suppressed stretch; disposing the result ends it, after a short tail for what is still on its way.</summary>
    public static IDisposable Begin()
    {
        Interlocked.Increment(ref _active);
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Volatile.Write(ref _quietAt, Environment.TickCount64 + TailMilliseconds);
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
