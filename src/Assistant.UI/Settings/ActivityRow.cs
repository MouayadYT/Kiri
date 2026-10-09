using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using Assistant.Core.Audit;
using Assistant.UI.Messages;
using Assistant.UI.ViewModels;

namespace Assistant.UI.Settings;

/// <summary>
/// One line of the activity page (PROJECT_SPEC §4.9, step 117): a run of the agent with its steps, or an action of its own, such as an integration that was installed.
/// It says when it happened, what it was, how it ended, what the user answered when asked, how long it took and, when a run could not go on, where; a run's steps can be
/// opened, and a run that goes on can be stopped. It is made of the same fixed words as the panel in the conversation and holds no private content.
/// </summary>
public sealed class ActivityRow : NotifyingObject
{
    private readonly Func<Guid, bool>? _cancel;
    private readonly TimeProvider _clock;
    private readonly RelayCommand _toggle;
    private readonly RelayCommand _cancelCommand;
    private ActivityItem _item;
    private bool _expanded;

    internal ActivityRow(ActivityItem item, TimeProvider clock, Func<Guid, bool>? cancel)
    {
        _item = item;
        _clock = clock;
        _cancel = cancel;
        _toggle = new RelayCommand(_ => IsExpanded = !IsExpanded, _ => HasSteps);
        _cancelCommand = new RelayCommand(_ => Cancel(), _ => CanCancel);
        Sync();
    }

    /// <summary>The id of the run or the action.</summary>
    public Guid Id => _item.Id;

    /// <summary>Whether the line is a run of the agent.</summary>
    public bool IsTask => _item.Task is not null;

    /// <summary>The steps of a run, in order; none for an action.</summary>
    public ObservableCollection<ActivityStepItem> Steps { get; } = [];

    /// <summary>What it was: for a run, what its first step was and how many more there were; for an action, what was done.</summary>
    public string Title
    {
        get
        {
            if (_item.Action is { } action)
            {
                return action.Summary;
            }

            var made = _item.Task!.Steps.Where(step => step.Status != AuditStatus.Skipped).ToList();
            return made.Count switch
            {
                0 => "A request with no tool run",
                1 => made[0].Summary,
                _ => $"{made[0].Summary} and {(made.Count - 1).ToString(CultureInfo.CurrentCulture)} more",
            };
        }
    }

    /// <summary>When it happened, how many steps it had and how long it took: "2:41 PM · 3 steps · 4.2 s".</summary>
    public string Detail
    {
        get
        {
            var now = _clock.GetUtcNow();
            var when = ActivityText.When(_item.StartedAt, now);
            if (_item.Task is { } task)
            {
                return $"{when} · {ActivityText.Steps(task.StepCount)}" + (task.IsRunning ? string.Empty : " · " + ActivityText.Duration(task.Elapsed(now)));
            }

            return _item.Action!.Duration is { } duration && _item.Action.Status != AuditStatus.Skipped ? $"{when} · {ActivityText.Duration(duration)}" : when;
        }
    }

    /// <summary>How it stands, or how it ended, in words.</summary>
    public string StatusText => _item.Task is { } task
        ? AuditText.TaskStatusText(task.Status)
        : AuditText.StatusText(_item.Action!.Status, _item.Action.Confirmation, _item.Action.ErrorCode);

    /// <summary>What the user answered when asked, for an action; empty otherwise.</summary>
    public string ConfirmationText => AuditText.ConfirmationText(_item.Action?.Confirmation) ?? string.Empty;

    /// <summary>Whether there is a <see cref="ConfirmationText"/>.</summary>
    public bool HasConfirmation => _item.Action?.Confirmation is not null;

    /// <summary>The line that says where a run could not go on; empty when there is none.</summary>
    public string FailurePoint => _item.Task?.FailurePoint ?? string.Empty;

    /// <summary>Whether there is a <see cref="FailurePoint"/>.</summary>
    public bool HasFailure => _item.Task?.FailurePoint is not null;

    /// <summary>Whether it goes on.</summary>
    public bool IsRunning => _item.Task?.IsRunning ?? !_item.Action!.Status.IsFinished();

    /// <summary>Whether it ended in something that did not go as it should.</summary>
    public bool IsProblem => _item.Task is { } task
        ? task.Status is AgentTaskStatus.Incomplete or AgentTaskStatus.Cancelled or AgentTaskStatus.Failed or AgentTaskStatus.Interrupted
        : _item.Action!.Status.IsProblem();

    /// <summary>Whether it ended well.</summary>
    public bool IsDone => _item.Task is { } task ? task.Status == AgentTaskStatus.Completed : _item.Action!.Status == AuditStatus.Succeeded;

    /// <summary>Whether it has steps that can be opened.</summary>
    public bool HasSteps => Steps.Count > 0;

    /// <summary>Whether the steps are shown.</summary>
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (Set(ref _expanded, value && HasSteps))
            {
                OnPropertyChanged(nameof(ToggleLabel));
            }
        }
    }

    /// <summary>What the button that opens and closes the steps says.</summary>
    public string ToggleLabel => _expanded ? "Hide steps" : "Show steps";

    /// <summary>Opens or closes the steps.</summary>
    public ICommand ToggleCommand => _toggle;

    /// <summary>Whether a run that goes on can be stopped from here: it has not been asked to stop already.</summary>
    public bool CanCancel => _cancel is not null && _item.Task is { IsRunning: true, CancelRequested: false };

    /// <summary>Stops a run that goes on.</summary>
    public ICommand CancelCommand => _cancelCommand;

    /// <summary>What the Cancel button says.</summary>
    public string CancelLabel => _item.Task is { CancelRequested: true } ? "Stopping…" : "Cancel";

    /// <summary>The line as one text, for assistive technology.</summary>
    public string Text =>
        $"{Title}. {Detail}. {StatusText}." + (HasConfirmation ? " " + ConfirmationText + "." : string.Empty) + (HasFailure ? " " + FailurePoint + "." : string.Empty);

    /// <summary>Shows the run or action as it is now: its steps are updated where they stand, so what the user has open stays open.</summary>
    internal void Show(ActivityItem item)
    {
        _item = item;
        Sync();
        OnPropertyChanged(string.Empty);
    }

    private void Sync()
    {
        var steps = _item.Task?.Steps ?? [];
        for (var index = 0; index < steps.Count; index++)
        {
            if (index < Steps.Count)
            {
                Steps[index].Show(steps[index]);
            }
            else
            {
                Steps.Add(new ActivityStepItem(steps[index]));
            }
        }

        while (Steps.Count > steps.Count)
        {
            Steps.RemoveAt(Steps.Count - 1);
        }

        if (_expanded && !HasSteps)
        {
            _expanded = false;
        }

        _toggle.RaiseCanExecuteChanged();
        _cancelCommand.RaiseCanExecuteChanged();
    }

    private void Cancel()
    {
        if (CanCancel)
        {
            _cancel!(Id);
        }
    }
}
