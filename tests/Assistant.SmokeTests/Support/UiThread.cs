using System.Windows;
using System.Windows.Threading;

namespace Assistant.SmokeTests.Support;

/// <summary>
/// The one thread the app's windows and view models live on, with the application's theme loaded as the app loads it. A check's body runs on it,
/// so what it makes (windows, view models, the hotkey's message window) belongs to it, and its awaits come back to it.
/// </summary>
internal static class UiThread
{
    /// <summary>The longest a check's body may take: a hang is a failure that says so, not a test run that never ends.</summary>
    public static readonly TimeSpan DefaultLimit = TimeSpan.FromMinutes(2);

    private static readonly Lazy<Dispatcher> Started = new(Start);

    public static Dispatcher Dispatcher => Started.Value;

    public static async Task RunAsync(Func<Task> body, TimeSpan? limit = null)
    {
        var running = Dispatcher.InvokeAsync(body).Task.Unwrap();
        var finished = await Task.WhenAny(running, Task.Delay(limit ?? DefaultLimit));
        if (finished != running)
        {
            throw new TimeoutException("The check did not finish in time.");
        }

        await running;
    }

    private static Dispatcher Start()
    {
        using var ready = new ManualResetEventSlim();
        Dispatcher? dispatcher = null;
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/Assistant.UI;component/Themes/Theme.xaml", UriKind.Relative),
            });
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true, Name = "Smoke UI" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The WPF dispatcher did not start.");
        }

        return dispatcher!;
    }
}
