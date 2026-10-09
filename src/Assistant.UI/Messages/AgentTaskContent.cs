using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using System.Windows.Threading;
using Assistant.Core.Audit;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Messages;

/// <summary>
/// The panel in the conversation for a run of the agent that has several steps, or in which something went wrong (PROJECT_SPEC §4.8, step 117): each tool the Assistant
/// calls as a line (what it is, how it stands, what the user answered when asked, how long it took), a button that stops the run, and, when the run could not go on,
/// the one short line that says where. It follows the run live and is for the moment: it is not saved with the answer, which is what the activity page is for.
/// </summary>
/// <remarks>
/// Everything it shows is made of fixed words and the tool's name, from the run's <see cref="AgentTaskSnapshot"/>, which holds no private content: not what a tool was
/// called with, not what it returned. The Cancel button asks the run to stop exactly as the Stop button of the conversation does: what is going on is told to stop, nothing
/// more is started, and what was done stays done. The run may change from any thread; the panel follows it on its own.
/// </remarks>
public sealed class AgentTaskContent : MessageContent, INotifyPropertyChanged, IDisposable
{
    private readonly IAgentTaskView _task;
    private readonly Dispatcher _dispatcher;
    private readonly TimeProvider _clock;
    private readonly RelayCommand _cancelCommand;
    private readonly RelayCommand? _openActivityCommand;
    private AgentTaskSnapshot _snapshot;
    private bool _disposed;

    /// <summary>Creates the panel for <paramref name="task"/>. It must be created on the user interface's thread, which is where it is shown.</summary>
    /// <param name="task">The run to follow.</param>
    /// <param name="clock">The time, for how long a run that goes on has taken.</param>
    /// <param name="openActivity">Opens the activity page; without it the panel has no link to it.</param>
    public AgentTaskContent(IAgentTaskView task, TimeProvider? clock = null, Action? openActivity = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        _task = task;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _clock = clock ?? TimeProvider.System;
        _snapshot = task.Snapshot;
        _cancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);
        _openActivityCommand = openActivity is null ? null : new RelayCommand(_ => openActivity());
        Refresh(_snapshot);
        _task.Changed += OnTaskChanged;

        // The run may have moved on between the snapshot and the subscription.
        if (!ReferenceEquals(_snapshot, _task.Snapshot))
        {
            Refresh(_task.Snapshot);
        }
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The panel is as wide as a card.</summary>
    public override bool IsWide => true;

    /// <summary>The steps are the answer's workings: they are behind the button with three dots under it, from the first step on.</summary>
    public override bool IsTucked => true;

    /// <summary>The steps, in order, the ones that were not made included.</summary>
    public ObservableCollection<ActivityStepItem> Steps { get; } = [];

    /// <summary>How the run stands, in a few words: "Working on it", "Done", "Stopped early".</summary>
    public string Title => AuditText.TaskStatusText(_snapshot.Status);

    /// <summary>The title with how many steps and how long: "3 steps · 4.2 s".</summary>
    public string Summary =>
        ActivityText.Steps(_snapshot.StepCount) + " · " + ActivityText.Duration(_snapshot.Elapsed(_clock.GetUtcNow()));

    /// <summary>Whether the run goes on.</summary>
    public bool IsRunning => _snapshot.IsRunning;

    /// <summary>Whether the run has ended, one way or another.</summary>
    public bool IsFinished => !IsRunning;

    /// <summary>Whether the run came to its answer.</summary>
    public bool IsDone => _snapshot.Status == AgentTaskStatus.Completed;

    /// <summary>Whether the user asked to stop the run and it has not stopped yet.</summary>
    public bool CancelRequested => _snapshot.CancelRequested;

    /// <summary>Whether the Cancel button works: the run goes on and has not been asked to stop.</summary>
    public bool CanCancel => IsRunning && !CancelRequested;

    /// <summary>The line that says where the run could not go on; empty when it went on to its end.</summary>
    public string FailurePoint => _snapshot.FailurePoint ?? string.Empty;

    /// <summary>Whether there is a <see cref="FailurePoint"/>.</summary>
    public bool HasFailure => _snapshot.FailurePoint is not null;

    /// <summary>What the Cancel button says: it says that the run is stopping once it was pressed.</summary>
    public string CancelLabel => CancelRequested ? "Stopping…" : "Cancel";

    /// <summary>Stops the run.</summary>
    public ICommand CancelCommand => _cancelCommand;

    /// <summary>Opens the activity page.</summary>
    public ICommand? OpenActivityCommand => _openActivityCommand;

    /// <summary>Whether the panel can open the activity page.</summary>
    public bool HasOpenActivity => _openActivityCommand is not null;

    /// <summary>The run as it stands, for tests and for assistive technology.</summary>
    public AgentTaskSnapshot Snapshot => _snapshot;

    /// <summary>Everything the panel says as one text, for assistive technology.</summary>
    public string Text =>
        Title + ". " + Summary + ". " + string.Join(" ", Steps.Select(step => step.Text)) + (HasFailure ? " " + FailurePoint + "." : string.Empty);

    /// <summary>Asks the run to stop, if it goes on and has not been asked already.</summary>
    public void Cancel()
    {
        if (CanCancel)
        {
            _task.Cancel();
        }
    }

    /// <summary>Stops following the run.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _task.Changed -= OnTaskChanged;
    }

    // The run changes from the thread that runs it: the panel follows on its own.
    private void OnTaskChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var snapshot = _task.Snapshot;
        if (_dispatcher.CheckAccess())
        {
            Refresh(snapshot);
        }
        else
        {
            _ = _dispatcher.InvokeAsync(() =>
            {
                // Whichever change was seen last wins: a snapshot that was overtaken is not shown.
                if (!_disposed)
                {
                    Refresh(_task.Snapshot);
                }
            });
        }
    }

    private void Refresh(AgentTaskSnapshot snapshot)
    {
        _snapshot = snapshot;
        for (var index = 0; index < snapshot.Steps.Count; index++)
        {
            var entry = snapshot.Steps[index];
            if (index < Steps.Count)
            {
                Steps[index].Show(entry);
            }
            else
            {
                Steps.Add(new ActivityStepItem(entry));
            }
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsFinished));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(CancelRequested));
        OnPropertyChanged(nameof(CanCancel));
        OnPropertyChanged(nameof(CancelLabel));
        OnPropertyChanged(nameof(FailurePoint));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(Text));
        _cancelCommand.RaiseCanExecuteChanged();
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
