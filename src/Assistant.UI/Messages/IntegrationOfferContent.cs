using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Assistant.Core.Domain;
using Assistant.Tools.Integrations;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Messages;

/// <summary>Where an offer to install an integration stands.</summary>
public enum IntegrationOfferState
{
    /// <summary>Shown, waiting for the user to click Install or Cancel. Nothing has been downloaded or run.</summary>
    Waiting = 0,

    /// <summary>The user approved it and it is being installed.</summary>
    Installing = 1,

    /// <summary>It is installed (or updated).</summary>
    Installed = 2,

    /// <summary>It was not installed: the reason is in <see cref="IntegrationOfferContent.ResultText"/>. Nothing it downloaded is left.</summary>
    Failed = 3,

    /// <summary>The user said no, before or during the installation. Nothing is left.</summary>
    Cancelled = 4,
}

/// <summary>
/// The approval panel for an integration the Assistant found and reviewed (PROJECT_SPEC §4.8, step 108): a panel in the conversation that tells the user what was
/// found, whether it is official or community-made, what it does, where it comes from, what account and access it may need, and what has to be
/// downloaded, and offers Install and Cancel. Nothing is downloaded or run until the user clicks Install; the click is the only thing in the app
/// that accepts the offer. While it installs the panel shows each step, with Cancel; when it ends it says whether the integration is installed and
/// where to see it. It is not saved with the answer: it is only for the moment. When the offer was made for a request (step 110), what the user decides goes
/// back to it: once the integration is installed the Assistant carries on with the request by itself, and when the user turns it down, cancels or it fails the
/// Assistant says that it was not installed and the request was not carried out (<see cref="ContinuationRequested"/>).
/// </summary>
public sealed class IntegrationOfferContent : MessageContent, INotifyPropertyChanged, IContinuingContent
{
    private readonly Func<IProgress<InstallProgress>, CancellationToken, Task<InstallOutcome>> _install;
    private readonly Action _decline;
    private readonly Action? _openSettings;
    private readonly PendingRequest? _pending;
    private readonly RelayCommand _installCommand;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand _openSettingsCommand;
    private CancellationTokenSource? _running;
    private IntegrationOfferState _state;
    private string _progressText = string.Empty;
    private double? _progressFraction;
    private string _resultText = string.Empty;
    private bool _continued;

    /// <summary>Creates the panel for <paramref name="offer"/>.</summary>
    /// <param name="offer">What the user is shown.</param>
    /// <param name="install">Installs it, reporting progress, when the user clicks Install; the only way the offer is accepted.</param>
    /// <param name="decline">Tells that the user turned it down before it was installed.</param>
    /// <param name="openSettings">Opens Settings where the installed integrations are listed; without it the panel has no such button.</param>
    /// <param name="pending">The request the offer was made for, which goes on (or is said not to) once the user has decided; without it nothing goes back.</param>
    public IntegrationOfferContent(
        IntegrationOffer offer, Func<IProgress<InstallProgress>, CancellationToken, Task<InstallOutcome>> install, Action decline, Action? openSettings = null,
        PendingRequest? pending = null)
    {
        ArgumentNullException.ThrowIfNull(offer);
        ArgumentNullException.ThrowIfNull(install);
        ArgumentNullException.ThrowIfNull(decline);
        Offer = offer;
        _install = install;
        _decline = decline;
        _openSettings = openSettings;
        _pending = pending;
        _installCommand = new RelayCommand(_ => _ = InstallAsync(), _ => State == IntegrationOfferState.Waiting);
        _cancelCommand = new RelayCommand(_ => Cancel(), _ => State is IntegrationOfferState.Waiting or IntegrationOfferState.Installing);
        _openSettingsCommand = new RelayCommand(_ => _openSettings?.Invoke(), _ => _openSettings is not null);
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <inheritdoc/>
    /// <remarks>
    /// Raised when the offer ends, whichever way, and only when it was made for a request: installed, the request goes on; turned down, cancelled or failed,
    /// it is said not to have been carried out. Once, however many times the panel is pressed.
    /// </remarks>
    public event EventHandler<PendingContinuation>? ContinuationRequested;

    /// <summary>The panel is as wide as a card.</summary>
    public override bool IsWide => true;

    /// <summary>What the user is shown.</summary>
    public IntegrationOffer Offer { get; }

    /// <summary>The question the panel asks: "Install the Todoist integration?" or "Update Todoist to version 13.4.0?".</summary>
    public string Title => Offer.Kind switch
    {
        IntegrationOfferKind.Update => $"Update {Offer.AppName}{(Offer.NewVersion is { } version ? " to version " + version : string.Empty)}?",
        IntegrationOfferKind.Connect => $"Connect {Offer.AppName}?",
        IntegrationOfferKind.SignIn => $"Sign in to {Offer.AppName}?",
        _ => $"Install the {Offer.AppName} integration?",
    };

    /// <summary>The label of the button that approves it.</summary>
    public string InstallLabel => Offer.Kind switch
    {
        IntegrationOfferKind.Update => "Update",
        IntegrationOfferKind.Connect => "Connect",
        IntegrationOfferKind.SignIn => "Sign in",
        _ => "Install",
    };

    /// <summary>Who made it, in words.</summary>
    public string MakerText => Offer.MakerText;

    /// <summary>The short label of the maker badge: Official, Community, Unconfirmed or Unknown.</summary>
    public string MakerBadge => Offer.Maker switch
    {
        IntegrationOfferMaker.Official => "Official",
        IntegrationOfferMaker.Community => "Community-made",
        IntegrationOfferMaker.ClaimsOfficial => "Not confirmed official",
        _ => "Maker unknown",
    };

    /// <summary>Whether the maker is the app's own, which the badge is coloured for.</summary>
    public bool IsOfficial => Offer.Maker == IntegrationOfferMaker.Official;

    /// <summary>What it lets the Assistant do.</summary>
    public string Provides => Offer.Provides;

    /// <summary>Where it comes from.</summary>
    public string Source => Offer.Source;

    /// <summary>The account, keys and access it may need, one line each.</summary>
    public IReadOnlyList<string> Needs => Offer.Needs;

    /// <summary>What is downloaded and set up besides it, one line each.</summary>
    public IReadOnlyList<string> Requirements => Offer.Requirements;

    /// <summary>What could not be checked, one line each.</summary>
    public IReadOnlyList<string> Notes => Offer.Notes;

    /// <summary>Whether there is anything the review could not check.</summary>
    public bool HasNotes => Offer.Notes.Count > 0;

    /// <summary>Where the offer stands.</summary>
    public IntegrationOfferState State
    {
        get => _state;
        private set
        {
            if (_state == value)
            {
                return;
            }

            _state = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWaiting));
            OnPropertyChanged(nameof(IsInstalling));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(IsProblem));
            _installCommand.RaiseCanExecuteChanged();
            _cancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>Whether it is waiting for the user's answer.</summary>
    public bool IsWaiting => State == IntegrationOfferState.Waiting;

    /// <summary>Whether it is being installed.</summary>
    public bool IsInstalling => State == IntegrationOfferState.Installing;

    /// <summary>Whether it has ended, one way or another.</summary>
    public bool IsFinished => State is IntegrationOfferState.Installed or IntegrationOfferState.Failed or IntegrationOfferState.Cancelled;

    /// <summary>Whether it was installed.</summary>
    public bool IsInstalled => State == IntegrationOfferState.Installed;

    /// <summary>Whether it ended without being installed because something went wrong.</summary>
    public bool IsProblem => State == IntegrationOfferState.Failed;

    /// <summary>What is happening while it installs.</summary>
    public string ProgressText
    {
        get => _progressText;
        private set => Set(ref _progressText, value);
    }

    /// <summary>How much of the current step is done, from 0 to 1, or <see langword="null"/> when it cannot be told.</summary>
    public double? ProgressFraction
    {
        get => _progressFraction;
        private set
        {
            if (Set(ref _progressFraction, value))
            {
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(ProgressPercent));
            }
        }
    }

    /// <summary>Whether the progress bar should move without a value.</summary>
    public bool IsIndeterminate => ProgressFraction is null;

    /// <summary>The progress as a number from 0 to 100 for the bar.</summary>
    public double ProgressPercent => (ProgressFraction ?? 0) * 100;

    /// <summary>What happened, once it ended.</summary>
    public string ResultText
    {
        get => _resultText;
        private set => Set(ref _resultText, value);
    }

    /// <summary>Approves the offer: installs the integration. Only works while the offer waits.</summary>
    public ICommand InstallCommand => _installCommand;

    /// <summary>Turns the offer down while it waits, or stops the installation while it runs.</summary>
    public ICommand CancelCommand => _cancelCommand;

    /// <summary>Opens Settings where the installed integrations are listed.</summary>
    public ICommand OpenSettingsCommand => _openSettingsCommand;

    /// <summary>Whether the panel can open Settings.</summary>
    public bool CanOpenSettings => _openSettings is not null;

    /// <summary>Everything the panel says as one text, for assistive technology.</summary>
    public string Text =>
        $"{Title} {MakerText} {Provides} {Source} {string.Join(' ', Needs)} {string.Join(' ', Requirements)} {string.Join(' ', Notes)}".Trim();

    /// <summary>Approves the offer and installs the integration.</summary>
    public async Task InstallAsync()
    {
        if (State != IntegrationOfferState.Waiting)
        {
            return;
        }

        State = IntegrationOfferState.Installing;
        ProgressText = "Starting";
        ProgressFraction = null;
        _running = new CancellationTokenSource();
        var progress = new Progress<InstallProgress>(step =>
        {
            // A report that arrives after the installation ended is not what the panel is showing any more.
            if (State == IntegrationOfferState.Installing)
            {
                ProgressText = step.Message;
                ProgressFraction = step.Fraction;
            }
        });
        InstallOutcome outcome;
        try
        {
            outcome = await _install(progress, _running.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            outcome = InstallOutcome.Cancel();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            outcome = InstallOutcome.Fail(
                InstallFailure.SetupFailed,
                Offer.Kind is IntegrationOfferKind.Connect or IntegrationOfferKind.SignIn ? "Something went wrong, and nothing was connected." : "Something went wrong, and nothing was installed.");
        }
        finally
        {
            _running.Dispose();
            _running = null;
        }

        ResultText = outcome.Message;
        State = outcome.Status switch
        {
            InstallStatus.Installed => IntegrationOfferState.Installed,
            InstallStatus.Cancelled => IntegrationOfferState.Cancelled,
            _ => IntegrationOfferState.Failed,
        };
        GoBackToRequest(outcome.IsInstalled);
    }

    /// <summary>Turns the offer down, or stops the installation.</summary>
    public void Cancel()
    {
        switch (State)
        {
            case IntegrationOfferState.Waiting:
                _decline();
                ResultText = Offer.Kind is IntegrationOfferKind.Connect or IntegrationOfferKind.SignIn
                    ? "Cancelled. Nothing was connected."
                    : "Cancelled. Nothing was downloaded or installed.";
                State = IntegrationOfferState.Cancelled;
                GoBackToRequest(installed: false);
                break;
            case IntegrationOfferState.Installing:
                // The installer stops, deletes what it made and answers with a cancelled outcome, which ends the panel's wait.
                ProgressText = "Cancelling";
                _running?.Cancel();
                break;
        }
    }

    // The offer has ended: the request it was made for, if any, is told how (once).
    private void GoBackToRequest(bool installed)
    {
        if (_continued || _pending is null)
        {
            return;
        }

        _continued = true;
        ContinuationRequested?.Invoke(this, new PendingContinuation(_pending, installed ? PendingOutcome.Installed : PendingOutcome.NotInstalled));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>The panel's measure of progress as a percentage text for the bar's automation name.</summary>
    internal string ProgressDescription => ProgressFraction is { } fraction
        ? $"{ProgressText}, {(fraction * 100).ToString("0", CultureInfo.InvariantCulture)} percent"
        : ProgressText;
}
