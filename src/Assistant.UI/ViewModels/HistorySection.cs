using System.Globalization;

namespace Assistant.UI.ViewModels;

/// <summary>
/// A span of time the History window's list groups conversations by, under a header (PROJECT_SPEC §4.3): Today,
/// Yesterday, Previous 7 Days, Previous 30 Days, and then each earlier month. Sections compare by recency, the most
/// recent first, and are equal when they name the same span.
/// </summary>
/// <param name="Order">Where the section comes in the list, the most recent first.</param>
/// <param name="Title">What its header says.</param>
public sealed record HistorySection(int Order, string Title) : IComparable<HistorySection>, IComparable
{
    /// <summary>Conversations changed today.</summary>
    public static readonly HistorySection Today = new(0, "Today");

    /// <summary>Conversations changed yesterday.</summary>
    public static readonly HistorySection Yesterday = new(1, "Yesterday");

    /// <summary>Conversations changed two to seven days ago.</summary>
    public static readonly HistorySection PreviousSevenDays = new(2, "Previous 7 Days");

    /// <summary>Conversations changed eight to thirty days ago.</summary>
    public static readonly HistorySection PreviousThirtyDays = new(3, "Previous 30 Days");

    /// <summary>
    /// The section of a conversation that changed at <paramref name="at"/>, seen at <paramref name="now"/>. Days are
    /// counted in <paramref name="now"/>'s time zone, as a card's time is. Anything older than thirty days falls in the
    /// month it changed in, named with its year unless that is this year.
    /// </summary>
    public static HistorySection For(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToOffset(now.Offset);
        var days = (now.Date - local.Date).Days;
        if (days <= 0) return Today;
        if (days == 1) return Yesterday;
        if (days <= 7) return PreviousSevenDays;
        if (days <= 30) return PreviousThirtyDays;

        var monthsAgo = (now.Year - local.Year) * 12 + now.Month - local.Month;
        var format = local.Year == now.Year ? "MMMM" : "MMMM yyyy";
        return new HistorySection(PreviousThirtyDays.Order + monthsAgo, local.ToString(format, CultureInfo.CurrentCulture));
    }

    /// <inheritdoc/>
    public int CompareTo(HistorySection? other) => other is null ? 1 : Order.CompareTo(other.Order);

    /// <inheritdoc/>
    int IComparable.CompareTo(object? obj) => CompareTo(obj as HistorySection);

    /// <inheritdoc/>
    public override string ToString() => Title;
}
