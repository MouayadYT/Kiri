using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Assistant.Core.Storage;
using Assistant.Core.Contracts;
using Assistant.Core.Settings;
using Assistant.Core.Startup;
using Assistant.UI.Browser;
using Assistant.UI.Capture;
using Assistant.UI.Explorer;
using Assistant.UI.Selection;
using Assistant.UI.Tray;
using Assistant.UI.Views;
using Assistant.UI.Voice;
using Assistant.UI.Windowing;
using Assistant.Windows.Hotkeys;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.UI.Bootstrap;

/// <summary>
/// Starts the host, runs the WPF application with windows resolved from the container, and shuts both down.
/// </summary>
internal static class AppBootstrapper
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    /// <param name="arguments">The command line, without the program's name: <see cref="LaunchOptions.BackgroundArgument"/> when Windows started the app at sign-in.</param>
    public static int Run(string[]? arguments = null)
    {
        var launch = LaunchOptions.Parse(arguments);

        // Opened by the user while it is already running in the background: the one that runs shows its full window, and this start ends here, before
        // anything of its own is made. (A start in the background, with Windows or for a file sent to it, shows nothing and is not looked for.)
        if (!launch.StartHidden && Assistant.Windows.Startup.RunningAssistant.TryShowFullView(Assistant.Core.Ipc.AppPipe.ForCurrentUser()))
        {
            return 0;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        using var host = AppHost.Create(logToFile: true);
        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(AppBootstrapper));

        UnhandledExceptionEventHandler onUnhandled = (_, e) =>
            AppLog.UnhandledBackgroundException(logger, e.IsTerminating, e.ExceptionObject as Exception);
        EventHandler<UnobservedTaskExceptionEventArgs> onUnobserved = (_, e) =>
            AppLog.UnobservedTaskException(logger, e.Exception);
        AppDomain.CurrentDomain.UnhandledException += onUnhandled;
        TaskScheduler.UnobservedTaskException += onUnobserved;

        var environment = host.Services.GetRequiredService<IHostEnvironment>();
        AppLog.Starting(logger, typeof(AppBootstrapper).Assembly.GetName().Version, environment.EnvironmentName);

        int exitCode;
        try
        {
            var firstRun = host.Services.GetRequiredService<AppPaths>().EnsureDirectoriesExist();
            AppLog.LocalDataReady(logger, firstRun);

            host.Start();

            // The one read that waits: the shortcut is registered with Windows before the application runs. Nothing is on
            // screen yet and no dispatcher is running to be held up, and the service reads the file on the thread pool.
            var settings = host.Services.GetRequiredService<ISettingsService>().LoadAsync().GetAwaiter().GetResult();
            exitCode = RunApplication(host.Services, logger, startTimestamp, settings, launch, !settings.Ui.FirstRunCompleted);
        }
        catch (Exception exception)
        {
            // A failed start, or an exception that escapes the dispatcher, ends the application. Log it, then shut
            // down in order.
            AppLog.Fatal(logger, exception);
            exitCode = 1;
        }

        AppLog.Stopping(logger, exitCode);

        // Stop on a pool thread: the dispatcher has shut down, so continuations must not be posted back to it.
        Task.Run(() => host.StopAsync(ShutdownTimeout)).GetAwaiter().GetResult();

        AppLog.Stopped(logger, Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds);
        AppDomain.CurrentDomain.UnhandledException -= onUnhandled;
        TaskScheduler.UnobservedTaskException -= onUnobserved;
        return exitCode;
    }

    private static int RunApplication(
        IServiceProvider services, ILogger logger, long startTimestamp, AppSettings settings,
        LaunchOptions launch, bool showOnboarding)
    {
        // Windows are created after the application, so they can use its resources.
        var app = new App();
        app.InitializeComponent();
        app.SessionEnding += (_, e) => AppLog.SessionEnding(logger, e.ReasonSessionEnding);

        var lifetime = services.GetRequiredService<IHostApplicationLifetime>();
        using var stopRegistration = lifetime.ApplicationStopping.Register(() =>
        {
            AppLog.ShutdownRequested(logger);
            app.Dispatcher.InvokeAsync(() => app.Shutdown());
        });

        var window = services.GetRequiredService<AssistantWindow>();
        var controller = services.GetRequiredService<AssistantWindowStateController>();
        var history = services.GetRequiredService<HistoryWindowController>();
        var events = services.GetRequiredService<IAppEventBus>();

        // The Assistant's window is the application's: closing it ends the application, even while the History window
        // is hidden, as it only ever is until then.
        app.MainWindow = window;
        app.ShutdownMode = ShutdownMode.OnMainWindowClose;

        // A newer release is looked for while the Assistant runs, whatever the local model is doing: as it starts, and when the bar or the full window is
        // opened; GitHub is asked at most once in thirty days (GitHubUpdateChecker). Only here, in the running app, so that no test asks GitHub anything.
        var updates = services.GetRequiredService<Assistant.UI.Updates.UpdateNotifier>();
        history.Opened += (_, _) => updates.CheckIfDue();

        // The shortcut opens the Assistant, and pressed again puts it away.
        using var hotkeyBinding = new OverlayHotkeyBinding(
            window, services.GetRequiredService<GlobalHotkeyService>(), settings, events, saved => saved.Hotkeys.SearchOrAsk, () =>
            {
                controller.Toggle();
                updates.CheckIfDue();
            });

        // The second shortcut starts Visual Intelligence: the screen is captured and dimmed, and the user selects what to ask about.
        var visual = services.GetRequiredService<VisualIntelligenceController>();
        using var visualBinding = new OverlayHotkeyBinding(
            window, services.GetRequiredKeyedService<GlobalHotkeyService>(ServiceCollectionExtensions.VisualIntelligenceHotkey),
            settings, events, saved => saved.Hotkeys.VisualIntelligence, () => _ = visual.InvokeAsync());
        // The third shortcut reads the text selected in the application in front, before the Assistant takes the keyboard, and opens the Ask
        // panel with it and its quick actions.
        var selected = services.GetRequiredService<AskSelectionController>();
        using var selectedBinding = new OverlayHotkeyBinding(
            window, services.GetRequiredKeyedService<GlobalHotkeyService>(ServiceCollectionExtensions.SelectedTextHotkey),
            settings, events, saved => saved.Hotkeys.SelectedTextActions, () => _ = selected.InvokeAsync());

        // The fourth shortcut is the explicit fallback for apps that do not share their selection: it presses Copy in the app in front and puts
        // the clipboard back. It is registered with Windows only while the user has allowed it (Settings > Permissions, off by default), so
        // while it is off the keys stay with the other applications, and it never runs on its own.
        using var copyBinding = new OverlayHotkeyBinding(
            window, services.GetRequiredKeyedService<GlobalHotkeyService>(ServiceCollectionExtensions.SelectedTextByCopyHotkey),
            settings, events, saved => saved.Permissions.SelectedTextByCopy ? saved.Hotkeys.SelectedTextByCopy : null,
            () => _ = selected.InvokeByCopyAsync());

        // The Assistant's icon in the notification area (PROJECT_SPEC §4.9): its menu opens the Assistant, a new conversation or the Settings, pauses
        // the local AI and exits. It is the Assistant's way in while nothing is on screen, such as after a start with Windows.
        var tray = services.GetRequiredService<TrayController>();

        // The wake word (step 125): when the listener hears it, the Assistant opens and listens, on this thread.
        using var wakeWord = services.GetRequiredService<WakeWordController>();
        wakeWord.Start(app.Dispatcher);
        if (!launch.StartHidden)
        {
            window.ContentRendered += OnFirstRender;
        }

        app.Startup += (_, _) =>
        {
            tray.Start();

            // A Home Assistant that the last version kept as an MCP connection (which it mostly could not reach) becomes the one this version uses. Only the app
            // that is running does it, once, here.
            _ = services.GetService<Assistant.Core.Home.IHomeAssistant>()?.TakeOverEarlierAsync();
            if (showOnboarding)
            {
                new WindowInteropHelper(window).EnsureHandle();
                services.GetRequiredService<Assistant.UI.Onboarding.OnboardingController>().Show(controller.Invoke);
                return;
            }
            if (launch.StartHidden)
            {
                // Started with Windows: nothing is shown until the shortcut (or the icon) is used. The window is made but not shown, because the
                // shortcuts are registered with Windows through its handle. An update that is due is looked for now, as on any start (setup aside).
                new WindowInteropHelper(window).EnsureHandle();
                TrayLog.StartedHidden(logger, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
                updates.CheckIfDue();
                return;
            }

            // Opened by the user: the full window, as opening an app shows it. Closing it leaves the Assistant running in the notification area, and the
            // bar is the shortcut's. The Assistant's own window is made without being shown, because the shortcuts are registered through its handle.
            new WindowInteropHelper(window).EnsureHandle();
            window.ContentRendered -= OnFirstRender;
            history.ShowHistory();
            AppLog.Started(logger, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        };

        // Opened again while it runs (from Start or its shortcut): the start that was opened asks this one to show its full window, and ends.
        services.GetRequiredService<FullViewRequests>().Connect(history.ShowHistory, action => app.Dispatcher.BeginInvoke(action));

        // Files sent from File Explorer open the floating conversation, on the UI thread; ones that came while the app was
        // starting are opened once it runs.
        services.GetRequiredService<ExplorerFileRequests>().Connect(
            controller.OpenWithFiles, action => app.Dispatcher.BeginInvoke(action));

        // Text selected in a browser opens the same conversation with the text and where it is from attached (not the page); one that came
        // while the app was starting is opened once it runs.
        var browserSelections = services.GetRequiredService<AskBrowserSelectionController>();
        services.GetRequiredService<BrowserSelectionRequests>().Connect(
            (selection, browser) => _ = browserSelections.OpenAsync(selection, browser), action => app.Dispatcher.BeginInvoke(action));
        try
        {
            return app.Run();
        }
        finally
        {
            tray.Dispose();
        }

        void OnFirstRender(object? sender, EventArgs e)
        {
            window.ContentRendered -= OnFirstRender;
            AppLog.Started(logger, (long)Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds);
        }
    }
}
