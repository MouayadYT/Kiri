using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Assistant.Core.Updates;

namespace Assistant.UI.Updates;

/// <summary>The window that says a newer release is available (<see cref="UpdateNotifier"/>). <see cref="Opened"/> says whether the user chose its page.</summary>
internal sealed partial class UpdateAvailableWindow : Window
{
    public UpdateAvailableWindow(AvailableUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        InitializeComponent();
        Heading.Text = $"Kiri {update.NewVersion.TrimStart('v', 'V')} is available";
        Detail.Text = $"You are running {update.CurrentVersion}. The new version is on GitHub.";
        System.Windows.Automation.AutomationProperties.SetName(this, Heading.Text);
        OpenButton.Click += (_, _) =>
        {
            Opened = true;
            Close();
        };
        IgnoreButton.Click += (_, _) => Close();
    }

    /// <summary>Whether the user chose to open the release's page; otherwise the version is ignored.</summary>
    public bool Opened { get; private set; }

    internal System.Windows.Controls.Button OpenChoice => OpenButton;

    internal System.Windows.Controls.Button IgnoreChoice => IgnoreButton;
}

/// <summary>
/// Shows what the update check finds (<see cref="IUpdateChecker"/>). The check is started when the Assistant starts and whenever the user opens it
/// (<see cref="CheckIfDue"/>, called by the bootstrapper only), never on a timer; when it finds a newer release, the user is told once, in a small window
/// over the Assistant's: its page is opened in their browser, or that version is ignored from then on.
/// </summary>
internal sealed class UpdateNotifier : IDisposable
{
    private readonly IUpdateChecker _checker;
    private readonly Dispatcher _dispatcher;
    private bool _showing;

    public UpdateNotifier(IUpdateChecker checker, Dispatcher? dispatcher = null)
    {
        ArgumentNullException.ThrowIfNull(checker);
        _checker = checker;
        _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        _checker.UpdateAvailable += OnUpdateAvailable;
    }

    /// <summary>Shows the window for <paramref name="update"/> and returns whether its page was chosen; a test gives its own.</summary>
    internal Func<AvailableUpdate, bool> Ask { get; set; } = ShowWindow;

    /// <summary>Opens a page in the user's browser; a test gives its own.</summary>
    internal Action<string> OpenPage { get; set; } = OpenInBrowser;

    /// <summary>The Assistant started, or the user opened it: a check that is due (once in thirty days) starts in the background.</summary>
    public void CheckIfDue() => _checker.TriggerCheckIfDue();

    /// <inheritdoc/>
    public void Dispose() => _checker.UpdateAvailable -= OnUpdateAvailable;

    private void OnUpdateAvailable(object? sender, AvailableUpdate update) => _dispatcher.BeginInvoke(() => Show(update));

    /// <summary>Tells the user about <paramref name="update"/>: its page is opened, or it is ignored from now on.</summary>
    internal void Show(AvailableUpdate update)
    {
        if (_showing)
        {
            return;
        }

        _showing = true;
        try
        {
            if (Ask(update))
            {
                OpenPage(update.ReleaseUrl);
            }
            else
            {
                // Ignored, or closed without an answer: this version is not announced again.
                _ = _checker.DismissAsync(update.NewVersion);
            }
        }
        finally
        {
            _showing = false;
        }
    }

    private static bool ShowWindow(AvailableUpdate update)
    {
        var window = new UpdateAvailableWindow(update);

        // Over the Assistant's own window when one is open, so that it is that window's question and not a stray one.
        if (Application.Current?.Windows.OfType<Window>().FirstOrDefault(open => open.IsActive && open.IsVisible) is { } owner)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return window.Opened;
    }

    /// <summary>Opens <paramref name="url"/> with whatever the system opens web pages with. Only an https address is.</summary>
    internal static void OpenInBrowser(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            // Nothing opens web pages here: nothing happens.
        }
    }
}
