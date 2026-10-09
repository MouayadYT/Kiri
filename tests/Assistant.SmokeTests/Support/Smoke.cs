using Microsoft.Extensions.DependencyInjection;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// How a smoke check runs: on the app's UI thread, over a scratch folder, with the network watched (<see cref="NetworkWatch"/>) so that every check is
/// also a check that the Assistant stayed on this PC. The folder and what runs in it are gone afterwards.
/// </summary>
internal static class Smoke
{
    /// <summary>Runs <paramref name="body"/> against the app, started over a fresh data folder; <paramref name="replace"/> swaps an edge of its container.</summary>
    public static Task RunAsync(Func<SmokeApp, Task> body, Action<IServiceCollection>? replace = null, TimeSpan? limit = null) =>
        RunInFolderAsync(async scratch =>
        {
            await using var app = await SmokeApp.StartAsync(scratch, replace);
            await body(app);
        }, limit);

    /// <summary>Runs <paramref name="body"/> in a scratch folder, for a check that starts the app itself (more than once, or not at all).</summary>
    public static async Task RunInFolderAsync(Func<ScratchFolder, Task> body, TimeSpan? limit = null)
    {
        using var scratch = new ScratchFolder();
        var network = NetworkWatch.Start();
        await UiThread.RunAsync(() => body(scratch), limit);
        network.AssertSilent();
    }
}
