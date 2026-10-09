using System.Windows;
using System.Windows.Threading;
using Assistant.Core.Confirmation;
using Assistant.Core.Contracts;
using Assistant.Windows.Foreground;

namespace Assistant.UI.Permissions;

/// <summary>
/// The window that asks whether one use of a capability may go ahead (PROJECT_SPEC §4.9, step 119): what the Assistant wants to use, in the permission's own words and one line on
/// this use. <see cref="Decision"/> is <see cref="ConfirmationDecision.Approved"/> only when the user pressed Allow once, which works only once the window has been in front of them
/// for <see cref="ArmDelay"/> (so a key or click meant for the app they were in cannot answer it); closing it any other way, Esc and Enter included, is a no; and a window that is
/// not answered within <see cref="Lifetime"/> closes with <see cref="ConfirmationDecision.NoAnswer"/>. It holds no private content: its text is the permission's and the caller's one line.
/// </summary>
internal sealed partial class PermissionPromptWindow : Window
{
    /// <summary>How long the window must have been shown before Allow once works.</summary>
    public static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>How long the window waits for an answer.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private readonly DispatcherTimer _arming;
    private readonly DispatcherTimer _timeout;
    private ConfirmationDecision _decision = ConfirmationDecision.Declined;

    public PermissionPromptWindow(PermissionPromptRequest request, TimeSpan? armDelay = null, TimeSpan? lifetime = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        InitializeComponent();
        Heading.Text = request.Question;
        Detail.Text = request.Detail ?? string.Empty;
        Detail.Visibility = string.IsNullOrWhiteSpace(request.Detail) ? Visibility.Collapsed : Visibility.Visible;
        Setting.Text = "You chose to be asked each time. You can change this in Settings, under Permissions.";
        System.Windows.Automation.AutomationProperties.SetName(this, request.Question);

        AllowButton.Click += (_, _) =>
        {
            if (AllowButton.IsEnabled)
            {
                _decision = ConfirmationDecision.Approved;
                Close();
            }
        };
        DeclineButton.Click += (_, _) => Close();

        _arming = new DispatcherTimer { Interval = armDelay ?? ArmDelay };
        _arming.Tick += (_, _) =>
        {
            _arming.Stop();
            AllowButton.IsEnabled = true;
        };
        _timeout = new DispatcherTimer { Interval = lifetime ?? Lifetime };
        _timeout.Tick += (_, _) =>
        {
            _timeout.Stop();
            _decision = ConfirmationDecision.NoAnswer;
            Close();
        };
        Loaded += (_, _) =>
        {
            if ((armDelay ?? ArmDelay) <= TimeSpan.Zero)
            {
                AllowButton.IsEnabled = true;
            }
            else
            {
                _arming.Start();
            }

            _timeout.Start();
        };
        Closed += (_, _) =>
        {
            _arming.Stop();
            _timeout.Stop();
        };
    }

    /// <summary>How the question ended.</summary>
    public ConfirmationDecision Decision => _decision;

    /// <summary>The Allow once button.</summary>
    internal System.Windows.Controls.Button AllowChoice => AllowButton;

    /// <summary>The Don't allow button.</summary>
    internal System.Windows.Controls.Button DeclineChoice => DeclineButton;

    /// <summary>The window as the user sees it, to be answered.</summary>
    internal void Answer(ConfirmationDecision decision)
    {
        _decision = decision;
        Close();
    }
}

/// <summary>
/// Asks the user, in a window, whether a use of a capability that is set to ask every time may go ahead (<see cref="PermissionPromptWindow"/>). It asks on the user interface's thread
/// and waits; the window that was in front when it opened gets the keyboard back when it closes, since the question comes up in the middle of a shortcut that reads from that very
/// window. With no window to ask in, or when asking fails, the answer is that nobody could be asked, which is a no.
/// </summary>
internal sealed class WpfPermissionPrompt : IPermissionPrompt
{
    /// <inheritdoc/>
    public Task<ConfirmationDecision> AskAsync(PermissionPromptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            return Task.FromResult(ConfirmationDecision.CouldNotAsk);
        }

        return dispatcher.InvokeAsync(
            () =>
            {
                var previous = ForegroundWindow.Current();
                var window = new PermissionPromptWindow(request);
                using var _ = cancellationToken.Register(() => dispatcher.InvokeAsync(() => window.Answer(ConfirmationDecision.Declined)));
                window.ShowDialog();

                // The app the user was in is where the shortcut reads from: it is given the keyboard back before anything is read.
                ForegroundWindow.TryActivate(previous);
                return window.Decision;
            },
            DispatcherPriority.Normal,
            cancellationToken).Task;
    }
}
