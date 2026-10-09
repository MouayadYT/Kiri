namespace Assistant.SmokeTests.Support;

/// <summary>Waiting for what the app does in its own time, with a limit and a reason that says what never happened.</summary>
internal static class Wait
{
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(20);

    /// <summary>Checks every few milliseconds, on whatever context the caller is on (the UI thread, for a check's body).</summary>
    public static Task UntilAsync(Func<bool> condition, string failure, TimeSpan? limit = null) => UntilAsync(condition, () => failure, limit);

    /// <summary>As above, with a failure that is worked out when the time is up, so it can say how things stand then.</summary>
    public static async Task UntilAsync(Func<bool> condition, Func<string> failure, TimeSpan? limit = null)
    {
        var deadline = DateTime.UtcNow + (limit ?? DefaultLimit);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new Xunit.Sdk.XunitException(failure());
            }

            await Task.Delay(10);
        }
    }
}
