using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Assistant.Core.Audit;

namespace Assistant.UI.Messages;

/// <summary>
/// One step of what the Assistant did, as the panel in the conversation and the activity page list it (PROJECT_SPEC §4.8, §4.9, step 117): a number, what it was,
/// how it stands or ended in words, what the user answered when asked, and how long it took. Everything it shows comes from an <see cref="AuditEntry"/>, which holds no
/// private content, so nothing the user typed, no argument and no result is in a line of it. It follows its entry (<see cref="Show"/>): the same item is updated as a
/// step moves from working to waiting for the user to done.
/// </summary>
public sealed class ActivityStepItem : INotifyPropertyChanged
{
    private AuditEntry _entry;

    /// <summary>Creates the item for <paramref name="entry"/>.</summary>
    public ActivityStepItem(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entry = entry;
    }

    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The step's place in its run, from 1; 0 for an action of its own.</summary>
    public int Number => _entry.Sequence;

    /// <summary>Whether the line says its number: a step of a run has one, an action of its own does not.</summary>
    public bool HasNumber => _entry.Sequence > 0;

    /// <summary>What it was, in a few words: "Send a message".</summary>
    public string Summary => _entry.Summary;

    /// <summary>The tool's name, or what the action was done to, for the line of detail under the summary.</summary>
    public string Name => _entry.Name;

    /// <summary>Whether the line has a name to show that is not already in the summary.</summary>
    public bool HasName => _entry.Kind == AuditKind.ToolCall && _entry.Name.Length > 0;

    /// <summary>How it stands, or how it ended, in words.</summary>
    public string StatusText => AuditText.StatusText(_entry.Status, _entry.Confirmation, _entry.ErrorCode);

    /// <summary>What the user answered when asked, or empty when they were not.</summary>
    public string ConfirmationText => AuditText.ConfirmationText(_entry.Confirmation) ?? string.Empty;

    /// <summary>Whether the user was asked.</summary>
    public bool HasConfirmation => _entry.Confirmation is not null;

    /// <summary>How long it took, or empty while it goes on or when it was not run.</summary>
    public string DurationText => _entry.Duration is { } duration && _entry.Status != AuditStatus.Skipped ? ActivityText.Duration(duration) : string.Empty;

    /// <summary>Whether it goes on.</summary>
    public bool IsWorking => _entry.Status == AuditStatus.Running;

    /// <summary>Whether it waits for the user.</summary>
    public bool IsWaiting => _entry.Status == AuditStatus.WaitingForYou;

    /// <summary>Whether it was done.</summary>
    public bool IsDone => _entry.Status == AuditStatus.Succeeded;

    /// <summary>Whether it did not work, took too long, was stopped or was interrupted.</summary>
    public bool IsProblem => _entry.Status.IsProblem();

    /// <summary>Whether it was not done or not run and that was no failure: the user did not allow it, or the call was not made.</summary>
    public bool IsNotDone => _entry.Status is AuditStatus.Declined or AuditStatus.Skipped;

    /// <summary>The line as one text, for assistive technology.</summary>
    public string Text =>
        (HasNumber ? $"Step {Number.ToString(CultureInfo.InvariantCulture)}: " : string.Empty)
        + Summary + ". " + StatusText + (HasConfirmation ? ". " + ConfirmationText : string.Empty) + ".";

    /// <summary>The entry this item shows.</summary>
    public AuditEntry Entry => _entry;

    /// <summary>Shows the entry as it is now. Call it on the user interface's thread.</summary>
    public void Show(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry == _entry)
        {
            return;
        }

        _entry = entry;
        OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>The few ways the activity panel and page write a time or a length of time.</summary>
public static class ActivityText
{
    /// <summary>A length of time: <c>0.4 s</c>, <c>12 s</c>, <c>1 min 5 s</c>.</summary>
    public static string Duration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero)
        {
            duration = TimeSpan.Zero;
        }

        if (duration < TimeSpan.FromSeconds(10))
        {
            return duration.TotalSeconds.ToString("0.0", CultureInfo.CurrentCulture) + " s";
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return Math.Round(duration.TotalSeconds).ToString("0", CultureInfo.CurrentCulture) + " s";
        }

        var minutes = (int)duration.TotalMinutes;
        var seconds = duration.Seconds;
        return seconds == 0 ? $"{minutes.ToString(CultureInfo.CurrentCulture)} min" : $"{minutes.ToString(CultureInfo.CurrentCulture)} min {seconds.ToString(CultureInfo.CurrentCulture)} s";
    }

    /// <summary>How many steps, in words: <c>1 step</c>, <c>3 steps</c>.</summary>
    public static string Steps(int count) =>
        count == 1 ? "1 step" : count.ToString(CultureInfo.CurrentCulture) + " steps";

    /// <summary>When something began, for a line of the log: today's time, or the date and time.</summary>
    public static string When(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var local = startedAt.ToLocalTime();
        var today = now.ToLocalTime().Date;
        return local.Date == today
            ? local.ToString("t", CultureInfo.CurrentCulture)
            : local.Date == today.AddDays(-1)
                ? "Yesterday, " + local.ToString("t", CultureInfo.CurrentCulture)
                : local.ToString("d", CultureInfo.CurrentCulture) + ", " + local.ToString("t", CultureInfo.CurrentCulture);
    }
}
