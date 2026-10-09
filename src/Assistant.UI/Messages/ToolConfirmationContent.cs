using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Confirmation;
using Assistant.Core.Events;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Messages;

/// <summary>
/// The question the Assistant asks before it does something that changes anything (PROJECT_SPEC §4.8, step 115): a panel in the conversation that says what would be
/// done and to what, exactly (the person a name came to, the file, the application, the whole text of a message, every argument of a connected app's tool), and asks
/// the user to allow it or not. Nothing is done until the user clicks the button that says yes, and a click answers that one question only: it cannot be kept for a
/// later call. A question that is not answered in time, or whose answer was stopped, ends as a no and says so. It is for the moment: it is not saved with the answer.
/// </summary>
/// <remarks>
/// The button that says yes does not work for a moment after the panel appears (<see cref="DefaultArmDelay"/>), so that a click or a key meant for something the user was
/// doing a moment ago cannot approve what they have not read. The button that says no always works. The panel never takes the keyboard focus. All of what it shows is the
/// text of <see cref="ToolConfirmation"/>, which is made safe to show.
/// </remarks>
public sealed class ToolConfirmationContent : MessageContent, INotifyPropertyChanged, IDisposable
{
    /// <summary>How long after it appears the button that says yes is held back.</summary>
    public static readonly TimeSpan DefaultArmDelay = TimeSpan.FromMilliseconds(600);

    private readonly ToolConfirmationRequested _request;
    private readonly Dispatcher _dispatcher;
    private readonly RelayCommand _approveCommand;
    private readonly RelayCommand _alwaysCommand;
    private readonly RelayCommand _declineCommand;
    private readonly CancellationTokenSource _arming = new();
    private bool _isArmed;
    private string _messageDraft;

    /// <summary>Creates the panel for <paramref name="request"/>. It must be created on the user interface's thread, which is where it is shown.</summary>
    /// <param name="request">The question; the panel answers it, and follows it when it ends some other way (time, a stop).</param>
    /// <param name="armDelay">How long the button that says yes is held back; <see cref="DefaultArmDelay"/> when not given, and none when zero.</param>
    public ToolConfirmationContent(ToolConfirmationRequested request, TimeSpan? armDelay = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        _request = request;
        _messageDraft = request.Confirmation.Details.FirstOrDefault(detail => detail.Label == "Message")?.Value ?? string.Empty;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _approveCommand = new RelayCommand(_ => Approve(), _ => CanApprove);
        _alwaysCommand = new RelayCommand(_ => ApproveAlways(), _ => CanApprove && CanAlwaysAllow);
        _declineCommand = new RelayCommand(_ => Decline(), _ => IsWaiting);
        _request.StateChanged += OnStateChanged;

        var delay = armDelay ?? DefaultArmDelay;
        if (delay <= TimeSpan.Zero)
        {
            _isArmed = true;
        }
        else
        {
            _ = ArmAfterAsync(delay);
        }
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The panel is as wide as a card.</summary>
    public override bool IsWide => true;

    /// <summary>What the user is asked.</summary>
    public ToolConfirmation Confirmation => _request.Confirmation;

    /// <summary>The question: "Send this message to Omar?".</summary>
    public string Title => Confirmation.Title;

    /// <summary>What it is done to and with, exactly, one line after another.</summary>
    public IReadOnlyList<ConfirmationDetail> Details => Confirmation.Details;

    /// <summary>What the user should know before they say yes; empty when there is nothing.</summary>
    public string Warning => Confirmation.Warning ?? string.Empty;

    /// <summary>Whether there is a warning.</summary>
    public bool HasWarning => Confirmation.Warning is not null;

    /// <summary>What the button that says no says.</summary>
    public string DeclineLabel => Confirmation.DeclineLabel ?? "Don't allow";

    /// <summary>What the button that says no is called for assistive technology.</summary>
    public string DeclineName => Confirmation.DeclineLabel ?? "Don't allow, and do nothing";

    /// <summary>What the button that says yes says.</summary>
    public string ApproveLabel => Confirmation.ApproveLabel;

    /// <summary>Where the question stands.</summary>
    public ToolConfirmationState State => _request.State;

    /// <summary>Whether it waits for the user's answer.</summary>
    public bool IsWaiting => State == ToolConfirmationState.Pending;

    /// <summary>Whether it has ended, one way or another.</summary>
    public bool IsFinished => !IsWaiting;

    /// <summary>Whether the user said yes.</summary>
    public bool IsAllowed => State == ToolConfirmationState.Approved;

    /// <summary>Whether the button that says yes works yet.</summary>
    public bool IsArmed => _isArmed;

    /// <summary>Whether the button that says yes can be pressed: the question waits and has been in front of the user for a moment.</summary>
    public bool CanApprove => IsWaiting && IsArmed && (!CanEditMessage || !string.IsNullOrWhiteSpace(_messageDraft));

    /// <summary>
    /// Whether the question can be answered "Always allow": a yes to this call and to every later call of the same action, kept in Settings under
    /// Permissions. It is offered for what changes a setting of the PC, opens something, or changes something in a connected app or the home, and
    /// never for a message.
    /// </summary>
    public bool CanAlwaysAllow => _request.CanAlwaysAllow;

    /// <summary>
    /// Whether the question is about a message to send. It is then drawn as the message itself (the message reference): who it is for, with the mark of the
    /// service it goes through, what it says, and Cancel and Send.
    /// </summary>
    public bool IsMessage =>
        Confirmation.Kind == ConfirmationKind.SendMessage && Details.Any(detail => detail.Label == "To") && Details.Any(detail => detail.Label == "Message");

    /// <summary>Whether it is any other question, drawn line by line.</summary>
    public bool IsGeneral => !IsMessage;

    /// <summary>Who the message is for.</summary>
    public string MessageTo => Detail("To");

    /// <summary>The first letters of their name, for the disc where their picture would be.</summary>
    public string MessageInitials => MessageChannelMark.Initials(MessageTo);

    /// <summary>Where the message goes, in full: "iMessage chat with Sami in Beeper".</summary>
    public string MessageRoute => Detail("Through");

    /// <summary>The service it goes through, when the route names one that is known: which mark is drawn.</summary>
    public string MessageService => Assistant.Core.Messaging.MessagingServices.Find(MessageRoute);

    /// <summary>What is written under the name: the service, or else the route.</summary>
    public string MessageChannel => MessageService.Length > 0 ? MessageService : MessageRoute;

    /// <summary>The colours of the service's mark.</summary>
    public System.Windows.Media.Brush MessageMarkBackground => MessageChannelMark.Background(MessageService);

    /// <summary>The service's mark.</summary>
    public System.Windows.Media.Geometry MessageMarkGlyph => MessageChannelMark.Glyph(MessageService);

    /// <summary>What the message says, whole, as it was drafted.</summary>
    public string MessageText => Detail("Message");

    /// <summary>Whether the user may rewrite what the message says before they send it: the question came with a line that can be changed.</summary>
    public bool CanEditMessage => IsMessage && Confirmation.Edit is { Label: "Message" };

    /// <summary>The longest the message may be.</summary>
    public int MessageMaxLength => Confirmation.Edit?.MaxLength ?? 0;

    /// <summary>
    /// What the message says in the field the user can write in. It starts as what was drafted; what is sent is what stands here when they press Send,
    /// and an empty field sends nothing (the button waits for words).
    /// </summary>
    public string MessageDraft
    {
        get => _messageDraft;
        set
        {
            var text = value ?? string.Empty;
            if (text == _messageDraft || !IsWaiting)
            {
                return;
            }

            _messageDraft = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanApprove));
            _approveCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Whether the panel has nothing left to show: a message the user said yes to is shown as sent by what follows (the Assistant's words and the message
    /// as it went), so the question does not stay above it.
    /// </summary>
    public bool IsGone => IsMessage && IsAllowed;

    /// <summary>
    /// A question that has been answered (or ended some other way) is one of the answer's workings: it folds away behind the button with three dots
    /// under the answer. One about a message is not: it goes when the message is sent, and says that it was not when it was not.
    /// </summary>
    public override bool IsTucked => IsGeneral && IsFinished;

    /// <summary>How the question ended, in words, once it has.</summary>
    public string ResultText => State switch
    {
        ToolConfirmationState.Declined when IsMessage => "Not sent.",
        ToolConfirmationState.Expired when IsMessage => "No answer in time, so it was not sent.",
        ToolConfirmationState.Withdrawn when IsMessage => "Stopped before you answered. It was not sent.",
        ToolConfirmationState.Approved when _request.IsAlways => "Allowed, and not asked about again. You can change that in Settings, under Permissions.",
        ToolConfirmationState.Approved => Confirmation.ApprovedResult ?? "Allowed.",
        ToolConfirmationState.Declined => Confirmation.DeclinedResult ?? "Not allowed. Nothing was done.",
        ToolConfirmationState.Expired => "No answer in time, so nothing was done.",
        ToolConfirmationState.Withdrawn => "Stopped before you answered. Nothing was done.",
        _ => string.Empty,
    };

    /// <summary>Says yes to this one call. Works only while the question waits and once it has been shown for a moment.</summary>
    public ICommand ApproveCommand => _approveCommand;

    /// <summary>Says yes to this call and to the same action from now on. Works where the question offers it, when the button that says yes does.</summary>
    public ICommand AlwaysAllowCommand => _alwaysCommand;

    /// <summary>Says no. Works while the question waits.</summary>
    public ICommand DeclineCommand => _declineCommand;

    /// <summary>Everything the panel says as one text, for assistive technology.</summary>
    public string Text => Confirmation.AsText();

    /// <summary>Says yes to this one call, if the question waits and the button is armed.</summary>
    public void Approve()
    {
        if (CanApprove)
        {
            // What the user left in the field is what is sent: it is handed over before the yes, which is what runs the tool.
            if (CanEditMessage && _messageDraft != MessageText)
            {
                Confirmation.Edit!.Set(_messageDraft);
            }

            _request.Approve();
        }
    }

    /// <summary>Says yes to this call and to the same action from now on, if the question offers that, waits and the button is armed.</summary>
    public void ApproveAlways()
    {
        if (CanApprove && CanAlwaysAllow)
        {
            _request.ApproveAlways();
        }
    }

    /// <summary>Says no, if the question waits.</summary>
    public void Decline() => _request.Decline();

    /// <summary>Stops following the question.</summary>
    public void Dispose()
    {
        _arming.Cancel();
        _arming.Dispose();
        _request.StateChanged -= OnStateChanged;
    }

    private string Detail(string label) => Details.FirstOrDefault(detail => detail.Label == label)?.Value ?? string.Empty;

    private async Task ArmAfterAsync(TimeSpan delay)
    {
        try
        {
            await Task.Delay(delay, _arming.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _isArmed = true;
            OnPropertyChanged(nameof(IsArmed));
            Changed();
        });
    }

    // The question may end from another thread (its time is up, or the answer was stopped): the panel follows on its own.
    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (_dispatcher.CheckAccess())
        {
            Changed();
        }
        else
        {
            _ = _dispatcher.InvokeAsync(Changed);
        }
    }

    private void Changed()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(IsWaiting));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsAllowed));
        OnPropertyChanged(nameof(IsGone));
        OnPropertyChanged(nameof(IsTucked));
        OnPropertyChanged(nameof(CanApprove));
        OnPropertyChanged(nameof(ResultText));
        _approveCommand.RaiseCanExecuteChanged();
        _alwaysCommand.RaiseCanExecuteChanged();
        _declineCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
